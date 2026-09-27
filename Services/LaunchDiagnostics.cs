using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Utilities;

namespace GreenLuma_Manager.Services;

/// <summary>
/// Always-on diagnostic tracer for GreenLuma launch attempts.
///
/// Writes a single, section-delimited, easy-to-share trace to
/// <c>%LOCALAPPDATA%\GLM_Manager\logs\LaunchDiagnostics.log</c> so that
/// machine-specific launch failures (for example Steam exiting immediately
/// after injection) become observable.
///
/// Every run records: application/config/elevation context, process snapshots
/// taken around the Steam shutdown, the contents of DLLInjector.ini, the
/// injector's PID, stdout/stderr and exit code, and the full Steam process
/// lifecycle (appearance, elevation, premature exit with exit code and
/// lifetime, relaunch).
///
/// Tracing must never affect a launch: all failures are swallowed and no
/// exception ever escapes this class.
/// </summary>
internal static class LaunchDiagnostics
{
    private const long MaxLogBytes = 2 * 1024 * 1024;

    private static readonly string LogDir = Path.Combine(PathDetector.AppDataDir, "logs");
    private static readonly string LogPath = Path.Combine(LogDir, "LaunchDiagnostics.log");
    private static readonly string PrevLogPath = Path.Combine(LogDir, "LaunchDiagnostics.prev.log");
    private static readonly object Sync = new();

    /// <summary>Absolute path of the diagnostic log file.</summary>
    public static string FilePath => LogPath;

    /// <summary>Processes tracked during a Steam lifecycle watch.</summary>
    public static readonly string[] SteamProcessNames =
        ["steam", "steamwebhelper", "steamerrorfilereporter"];

    /// <summary>Marker files GreenLuma's DLLInjector creates (CreateFiles / FileToCreate_N).</summary>
    private static readonly string[] MarkerFiles = ["NoQuestion.bin", "StealthMode.bin"];

    // ── Run framing ───────────────────────────────────────────────────────────

    /// <summary>Starts a new, clearly delimited run block with a context dump.</summary>
    public static void BeginRun(Config config, GreenLumaInstallMethod method, string source)
    {
        Section($"LAUNCH RUN — {source}");
        Line($"started: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        Line($"manager version: {SafeVersion()}");
        Line($"elevated: {IsSelfElevated()}  user: {SafeUser()}  machine: {Environment.MachineName}");
        Line($"OS: {Environment.OSVersion}  process64: {Environment.Is64BitProcess}  CLR: {Environment.Version}");
        Line($"detected method: {method}");
        Line($"config.SteamPath: '{config.SteamPath}'");
        Line($"config.GreenLumaPath: '{config.GreenLumaPath}'");
        Line($"config.PreferredMode: '{config.PreferredMode}'");
        Line($"config.NoHook: {config.NoHook}  ReplaceSteamAutostart: {config.ReplaceSteamAutostart}  LastStealthAnyPath: '{config.LastStealthAnyPath}'");
        Line($"Steam.exe exists: {FileExistsSafe(Path.Combine(config.SteamPath, "Steam.exe"))}");
        Line($"DLLInjector.exe exists (SteamPath): {FileExistsSafe(Path.Combine(config.SteamPath, "DLLInjector.exe"))}");
        Line($"DLLInjector.exe exists (GreenLumaPath): {FileExistsSafe(Path.Combine(config.GreenLumaPath, "DLLInjector.exe"))}");
    }

    /// <summary>Closes the current run block with a verdict.</summary>
    public static void EndRun(bool success, string summary)
    {
        Line($"RESULT: {(success ? "SUCCESS" : "FAILURE")} — {summary}");
        Line($"finished: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        Blank();
    }

    // ── Primitive writers ─────────────────────────────────────────────────────

    public static void Step(string message) => Line(message);

    public static void Line(string message) =>
        Append($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");

    public static void Blank() => Append(Environment.NewLine);

    public static void Section(string title)
    {
        Append($"{Environment.NewLine}===== {title} [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] =====" +
               Environment.NewLine);
    }

    /// <summary>Writes a titled, indented multi-line block (e.g. a file's contents).</summary>
    public static void Block(string title, string? content)
    {
        Line($"--- {title} ---");
        if (string.IsNullOrWhiteSpace(content))
        {
            Line("  (empty)");
            return;
        }

        foreach (var raw in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            Line($"  | {raw}");
    }

    // ── Process inspection ────────────────────────────────────────────────────

    /// <summary>Logs a snapshot of every running process matching the given names.</summary>
    public static void SnapshotProcesses(string label, params string[] names)
    {
        Line($"--- process snapshot: {label} ---");
        var any = false;

        foreach (var name in names)
        {
            var processes = SafeGetProcesses(name);
            if (processes.Length == 0)
                continue;

            any = true;
            foreach (var process in processes)
            {
                Line($"  {DescribeProcess(process)}");
                process.Dispose();
            }
        }

        if (!any)
            Line("  (no matching processes)");
    }

    // ── Steam lifecycle watch ─────────────────────────────────────────────────

    /// <summary>
    /// Waits for <c>steam.exe</c> to appear, then verifies Steam is still running
    /// at the end of a stability window. Steam's bootstrap process hands off to a
    /// successor and exits normally, so tracking is keyed on the process NAME, not
    /// on a single PID: every instance seen is logged (appearance details plus a
    /// reliably-read exit code) and success means "some steam.exe is alive at the
    /// end". A still-quiet moment between bootstrap instances is tolerated via a
    /// short settle window instead of being reported as a crash. When a GreenLuma
    /// module is loaded into the surviving instance it is reported, and the
    /// injector's marker files are checked.
    /// </summary>
    /// <returns><c>true</c> if a steam.exe was still running at the end of the watch.</returns>
    public static bool WatchSteam(TimeSpan appearTimeout, TimeSpan stabilityWindow,
        string? steamPath = null, string? greenLumaPath = null)
    {
        Section("Steam lifecycle watch");
        var started = Stopwatch.StartNew();
        var tracked = new Dictionary<int, TrackedSteam>();
        var sawSteam = false;

        // Phase 1 — wait for the first steam.exe.
        var appearDeadline = DateTime.UtcNow + appearTimeout;
        while (DateTime.UtcNow < appearDeadline)
        {
            SampleSteam(tracked, out var anyAlive);
            if (anyAlive)
            {
                sawSteam = true;
                Line($"steam.exe appeared after {started.Elapsed.TotalSeconds:F2}s");
                break;
            }

            Thread.Sleep(200);
        }

        if (!sawSteam)
        {
            Line($"steam.exe did NOT appear within {appearTimeout.TotalSeconds:F0}s");
            LogInjectorArtifacts(steamPath, greenLumaPath);
            ReleaseHandles(tracked);
            return false;
        }

        // Phase 2 — stability window. Tolerates the bootstrap hand-off between PIDs.
        var stableUntil = DateTime.UtcNow + stabilityWindow;
        while (DateTime.UtcNow < stableUntil)
        {
            SampleSteam(tracked, out _);
            Thread.Sleep(200);
        }

        var alive = tracked.Values.Where(t => !t.ExitLogged).ToList();

        // Phase 3 — if nothing is alive right now, give Steam a short settle window
        // to bring up a successor before declaring failure (avoids false negatives).
        if (alive.Count == 0)
        {
            Line("no steam.exe alive at end of stability window — waiting up to 5s for a successor");
            var settleUntil = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < settleUntil)
            {
                SampleSteam(tracked, out var anyAlive);
                if (anyAlive)
                {
                    alive = tracked.Values.Where(t => !t.ExitLogged).ToList();
                    break;
                }

                Thread.Sleep(200);
            }
        }

        var running = alive.Count > 0;
        Line(running
            ? $"steam.exe RUNNING at end: PID(s) {string.Join(", ", alive.Select(a => a.Pid))} (instances seen: {tracked.Count})"
            : $"steam.exe NOT running at end (instances seen: {tracked.Count})");

        if (running)
            CheckGreenLumaModule(alive.Select(a => a.Pid).ToList());

        LogInjectorArtifacts(steamPath, greenLumaPath);
        ReleaseHandles(tracked);
        return running;
    }

    private sealed class TrackedSteam
    {
        public int Pid;
        public IntPtr Handle;
        public DateTime FirstSeenUtc;
        public bool ExitLogged;
    }

    /// <summary>
    /// Samples the current steam.exe set: logs new instances, and detects exits
    /// using a held process handle so the exit code can be read reliably
    /// (Process.ExitCode throws once the Process object is disposed).
    /// </summary>
    private static void SampleSteam(Dictionary<int, TrackedSteam> tracked, out bool anyAlive)
    {
        var processes = SafeGetProcesses("steam");
        var current = new HashSet<int>();

        foreach (var process in processes)
        {
            int pid;
            try { pid = process.Id; }
            catch { continue; }

            current.Add(pid);

            if (!tracked.ContainsKey(pid))
            {
                var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, pid);
                tracked[pid] = new TrackedSteam { Pid = pid, Handle = handle, FirstSeenUtc = DateTime.UtcNow };
                Line($"steam.exe appeared: {DescribeProcess(process)}");
            }
        }

        foreach (var process in processes) process.Dispose();

        anyAlive = false;
        foreach (var item in tracked.Values)
        {
            if (item.ExitLogged)
                continue;

            if (item.Handle != IntPtr.Zero)
            {
                if (WaitForSingleObject(item.Handle, 0) == 0)
                {
                    Line($"steam.exe PID={item.Pid} EXITED after " +
                         $"{(DateTime.UtcNow - item.FirstSeenUtc).TotalSeconds:F2}s (exitCode={ExitCodeOf(item.Handle)})");
                    item.ExitLogged = true;
                    CloseHandle(item.Handle);
                    item.Handle = IntPtr.Zero;
                }
                else
                {
                    anyAlive = true;
                }
            }
            else if (current.Contains(item.Pid))
            {
                anyAlive = true;
            }
            else
            {
                Line($"steam.exe PID={item.Pid} gone (no handle; exit code unavailable)");
                item.ExitLogged = true;
            }
        }
    }

    private static void CheckGreenLumaModule(List<int> pids)
    {
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                var modules = process.Modules;

                ProcessModule? hit = null;
                foreach (ProcessModule module in modules)
                {
                    if (module.ModuleName.Contains("GreenLuma", StringComparison.OrdinalIgnoreCase))
                    {
                        hit = module;
                        break;
                    }
                }

                Line(hit != null
                    ? $"GreenLuma module LOADED in steam.exe PID={pid}: {hit.ModuleName} ({hit.FileName})"
                    : $"GreenLuma module NOT found in steam.exe PID={pid} ({modules.Count} modules enumerated) — stealth mode may rename/unload it");
                return;
            }
            catch (Exception ex)
            {
                Line($"module enumeration for steam.exe PID={pid} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void LogInjectorArtifacts(string? steamPath, string? greenLumaPath)
    {
        Line("--- injector marker files ---");
        foreach (var dir in new[] { greenLumaPath, steamPath })
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            foreach (var name in MarkerFiles)
            {
                var path = Path.Combine(dir, name);
                Line($"  {path}: {FileExistsSafe(path)}");
            }
        }
    }

    private static void ReleaseHandles(Dictionary<int, TrackedSteam> tracked)
    {
        foreach (var item in tracked.Values)
        {
            if (item.Handle == IntPtr.Zero)
                continue;

            CloseHandle(item.Handle);
            item.Handle = IntPtr.Zero;
        }
    }

    private static string ExitCodeOf(IntPtr handle)
    {
        try
        {
            if (GetExitCodeProcess(handle, out var code))
                return code == StillActive ? "still-active" : code.ToString();
        }
        catch
        {
            // ignored
        }

        return "unknown";
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static void Append(string text)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                RotateIfNeeded();
                File.AppendAllText(LogPath, text);
            }
            catch
            {
                // Diagnostics must never interfere with a launch.
            }
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxLogBytes)
                File.Move(LogPath, PrevLogPath, overwrite: true);
        }
        catch
        {
            // ignored
        }
    }

    private static Process[] SafeGetProcesses(string name)
    {
        try { return Process.GetProcessesByName(name); }
        catch { return []; }
    }

    private static string DescribeProcess(Process process)
    {
        string name;
        try { name = process.ProcessName; } catch { name = "?"; }

        string path;
        try { path = process.MainModule?.FileName ?? "?"; }
        catch { path = "(access denied)"; }

        string start;
        try { start = process.StartTime.ToString("HH:mm:ss.fff"); }
        catch { start = "?"; }

        return $"{name} PID={SafePid(process)} start={start} elevated={IsProcessElevated(SafePid(process))} path={path}";
    }

    private static int SafePid(Process process)
    {
        try { return process.Id; }
        catch { return -1; }
    }

    private static bool FileExistsSafe(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    private static string SafeVersion()
    {
        try { return typeof(LaunchDiagnostics).Assembly.GetName().Version?.ToString() ?? "?"; }
        catch { return "?"; }
    }

    private static string SafeUser()
    {
        try { return $"{Environment.UserDomainName}\\{Environment.UserName}"; }
        catch { return "?"; }
    }

    private static bool IsSelfElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ── Process token elevation (P/Invoke) ────────────────────────────────────

    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int Synchronize = 0x00100000;
    private const int TokenQuery = 0x0008;
    private const int TokenElevation = 20;
    private const uint StillActive = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr processHandle, out uint exitCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Best-effort elevation check for an arbitrary process. Returns
    /// "elevated", "not-elevated", or "unknown" (e.g. access denied).
    /// </summary>
    private static string IsProcessElevated(int pid)
    {
        if (pid <= 0)
            return "unknown";

        var processHandle = IntPtr.Zero;
        var tokenHandle = IntPtr.Zero;
        var buffer = IntPtr.Zero;

        try
        {
            processHandle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (processHandle == IntPtr.Zero)
                return "unknown";

            if (!OpenProcessToken(processHandle, TokenQuery, out tokenHandle))
                return "unknown";

            buffer = Marshal.AllocHGlobal(sizeof(int));
            if (!GetTokenInformation(tokenHandle, TokenElevation, buffer, sizeof(int), out _))
                return "unknown";

            return Marshal.ReadInt32(buffer) != 0 ? "elevated" : "not-elevated";
        }
        catch
        {
            return "unknown";
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (tokenHandle != IntPtr.Zero) CloseHandle(tokenHandle);
            if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
        }
    }
}
