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
    private static int _activeRuns;

    /// <summary>Absolute path of the diagnostic log file.</summary>
    public static string FilePath => LogPath;

    /// <summary>Processes tracked during a Steam lifecycle watch.</summary>
    public static readonly string[] SteamProcessNames =
        ["steam", "steamwebhelper", "steamerrorfilereporter"];

    /// <summary>Marker files GreenLuma's DLLInjector creates (CreateFiles / FileToCreate_N).</summary>
    private static readonly string[] MarkerFiles = ["NoQuestion.bin", "StealthMode.bin"];

    /// <summary>Steam's own startup logs, which explain an unexpected client exit.</summary>
    private static readonly string[] SteamLogFiles = ["bootstrap_log.txt", "console_log.txt"];

    private const int MaxLogTailLines = 60;

    // Launch-watch budgets. Deliberately generous so a slow / cold Steam start on a
    // mid-range PC is never misread as a failure (and never triggers a kill+retry).
    public static readonly TimeSpan SteamAppearTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan SteamAbsenceTolerance = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SteamStabilityDuration = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SteamOverallBudget = TimeSpan.FromSeconds(120);

    // ── Run framing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a new, clearly delimited run block with a context dump.
    /// Returns <c>false</c> if another launch is already in progress — a concurrent
    /// launch must not proceed, because its pre-launch Steam kill would terminate
    /// the Steam the running launch just started.
    /// </summary>
    public static bool BeginRun(Config config, GreenLumaInstallMethod method, string source)
    {
        var active = Interlocked.Increment(ref _activeRuns);
        if (active > 1)
        {
            Section($"CONCURRENT LAUNCH ATTEMPT — {source}");
            Line($"!! {active} launches are now active — refusing this one so it cannot kill the running launch's Steam");
            Interlocked.Decrement(ref _activeRuns);
            return false;
        }

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
        return true;
    }

    /// <summary>Closes the current run block with a verdict.</summary>
    public static void EndRun(bool success, string summary)
    {
        Line($"RESULT: {(success ? "SUCCESS" : "FAILURE")} — {summary}");
        Line($"finished: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        Blank();

        if (Interlocked.Decrement(ref _activeRuns) < 0)
            Interlocked.Exchange(ref _activeRuns, 0);
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
    /// Waits for <c>steam.exe</c> to appear, then decides whether Steam "launched".
    ///
    /// Steam's bootstrap hands off to a successor and exits normally, and a slow start
    /// can take a long time (update check, checksum verify, CEF/steamwebhelper). So the
    /// watch waits up to <paramref name="appearTimeout"/> for the first steam.exe, then
    /// keeps watching until either Steam has been alive continuously for
    /// <paramref name="stabilityDuration"/> (treat as launched), it has been absent for
    /// longer than <paramref name="absenceTolerance"/> (treat as exited), or
    /// <paramref name="overallBudget"/> expires. Success means a steam.exe is alive now
    /// or was alive within the absence tolerance. Tracking is keyed on the process NAME
    /// (not a single PID) so a slow hand-off is tolerated rather than misread as a crash.
    /// </summary>
    public static bool WatchSteam(TimeSpan appearTimeout, TimeSpan absenceTolerance,
        TimeSpan stabilityDuration, TimeSpan overallBudget,
        string? steamPath = null, string? greenLumaPath = null,
        CancellationToken cancellationToken = default)
    {
        Section("Steam lifecycle watch");
        var startedAt = DateTime.UtcNow;
        var started = Stopwatch.StartNew();
        var tracked = new Dictionary<int, TrackedSteam>();
        var appeared = false;

        // Phase 1 — wait for the first steam.exe to appear.
        var appearDeadline = startedAt + appearTimeout;
        while (DateTime.UtcNow < appearDeadline)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Line("Launch cancelled by user while waiting for steam.exe to appear");
                ReleaseHandles(tracked);
                return false;
            }

            SampleSteam(tracked, out var anyAlive);
            if (anyAlive)
            {
                appeared = true;
                Line($"steam.exe appeared after {started.Elapsed.TotalSeconds:F2}s");
                break;
            }

            Thread.Sleep(200);
        }

        if (!appeared)
        {
            Line($"steam.exe did NOT appear within {appearTimeout.TotalSeconds:F0}s");
            LogInjectorArtifacts(steamPath, greenLumaPath);
            LogSteamLogs(steamPath);
            ReleaseHandles(tracked);
            return false;
        }

        // Phase 2 — wait for Steam to settle: alive continuously for stabilityDuration,
        // or give up once it has been absent longer than absenceTolerance, or budget out.
        var budgetDeadline = startedAt + overallBudget;
        var lastAliveUtc = DateTime.UtcNow;
        var stableSinceUtc = DateTime.UtcNow;
        var moduleChecked = false;

        while (DateTime.UtcNow < budgetDeadline)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Line("Launch cancelled by user during Steam lifecycle watch");
                LogInjectorArtifacts(steamPath, greenLumaPath);
                LogSteamLogs(steamPath);
                ReleaseHandles(tracked);
                return false;
            }

            SampleSteam(tracked, out var anyAlive);

            if (anyAlive)
            {
                lastAliveUtc = DateTime.UtcNow;

                if (DateTime.UtcNow - stableSinceUtc >= stabilityDuration)
                {
                    Line($"steam.exe alive continuously for {stabilityDuration.TotalSeconds:F0}s — treating as launched");
                    break;
                }
            }
            else
            {
                stableSinceUtc = DateTime.UtcNow; // a gap restarts the stability streak

                if (DateTime.UtcNow - lastAliveUtc > absenceTolerance)
                {
                    Line($"steam.exe absent for more than {absenceTolerance.TotalSeconds:F0}s — treating as exited");
                    break;
                }
            }

            // Confirm injection while Steam is still alive — it may exit moments later,
            // and an end-of-watch check would then never run.
            if (!moduleChecked && DateTime.UtcNow - startedAt >= TimeSpan.FromSeconds(2))
            {
                var aliveNow = AlivePids(tracked);
                if (aliveNow.Count > 0)
                {
                    CheckGreenLumaModule(aliveNow);
                    moduleChecked = true;
                }
            }

            Thread.Sleep(200);
        }

        var alive = AlivePids(tracked);
        var absentFor = DateTime.UtcNow - lastAliveUtc;
        var running = alive.Count > 0 || absentFor <= absenceTolerance;

        Line(running
            ? $"steam.exe RUNNING: PID(s) {string.Join(", ", alive)} (instances seen: {tracked.Count})"
            : $"steam.exe NOT running (instances seen: {tracked.Count}, absent {absentFor.TotalSeconds:F1}s)");

        if (running && !moduleChecked && alive.Count > 0)
            CheckGreenLumaModule(alive);

        LogInjectorArtifacts(steamPath, greenLumaPath);
        LogSteamLogs(steamPath);
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

    /// <summary>PIDs of tracked steam.exe instances not observed to have exited.</summary>
    private static List<int> AlivePids(Dictionary<int, TrackedSteam> tracked)
        => tracked.Values.Where(t => !t.ExitLogged).Select(t => t.Pid).ToList();

    /// <summary>
    /// Dumps the tail of Steam's own startup logs so an unexpected exit can be
    /// explained by Steam itself rather than inferred from the process lifecycle.
    /// </summary>
    private static void LogSteamLogs(string? steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath))
            return;

        var logsDir = Path.Combine(steamPath, "logs");
        Line($"--- Steam logs ({logsDir}) ---");

        if (!Directory.Exists(logsDir))
        {
            Line("  (no logs directory)");
            return;
        }

        foreach (var name in SteamLogFiles)
        {
            var path = Path.Combine(logsDir, name);
            if (!FileExistsSafe(path))
            {
                Line($"  {name}: (missing)");
                continue;
            }

            Block($"tail of {name} (last {MaxLogTailLines} lines)", TailFile(path, MaxLogTailLines));
        }
    }

    private static string TailFile(string path, int maxLines)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n');
            return string.Join(Environment.NewLine, lines.TakeLast(maxLines));
        }
        catch (Exception ex)
        {
            return $"(read failed: {ex.GetType().Name}: {ex.Message})";
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
