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
    /// Waits for <c>steam.exe</c> to appear, then verifies it survives a short
    /// stability window. Every transition is logged: appearance, helper
    /// processes, premature exit (with exit code and lifetime), and relaunch.
    /// </summary>
    /// <returns><c>true</c> if Steam appeared and was still alive at the end of the stability window.</returns>
    public static bool WatchSteam(TimeSpan appearTimeout, TimeSpan stabilityWindow)
    {
        var waited = Stopwatch.StartNew();
        Process? main = null;
        var appearedAt = DateTime.UtcNow;

        // Phase 1 — wait for steam.exe to appear.
        var deadline = DateTime.UtcNow + appearTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var candidates = SafeGetProcesses("steam");
            if (candidates.Length > 0)
            {
                main = candidates[0];
                for (var i = 1; i < candidates.Length; i++) candidates[i].Dispose();
                appearedAt = DateTime.UtcNow;
                Line($"steam.exe appeared after {waited.Elapsed.TotalSeconds:F2}s: {DescribeProcess(main)}");
                break;
            }

            Thread.Sleep(200);
        }

        if (main == null)
        {
            Line($"steam.exe did NOT appear within {appearTimeout.TotalSeconds:F0}s");
            return false;
        }

        // Phase 2 — stability window: did it survive?
        var seenHelpers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "steam" };
        var stableUntil = DateTime.UtcNow + stabilityWindow;
        var alive = true;

        while (DateTime.UtcNow < stableUntil)
        {
            ReportNewHelpers(seenHelpers);

            var exited = false;
            try { exited = main.HasExited; }
            catch { /* process may be inaccessible; treat as alive */ }

            if (exited)
            {
                Line($"!! steam.exe PID={SafePid(main)} EXITED after " +
                     $"{(DateTime.UtcNow - appearedAt).TotalSeconds:F2}s (exitCode={SafeExitCode(main)})");

                var relaunched = SafeGetProcesses("steam");
                if (relaunched.Length > 0)
                {
                    main.Dispose();
                    main = relaunched[0];
                    for (var i = 1; i < relaunched.Length; i++) relaunched[i].Dispose();
                    appearedAt = DateTime.UtcNow;
                    Line($"steam.exe re-appeared: {DescribeProcess(main)}");
                }
                else
                {
                    alive = false;
                    break;
                }
            }

            Thread.Sleep(200);
        }

        if (alive)
            Line($"steam.exe PID={SafePid(main)} still alive after {stabilityWindow.TotalSeconds:F0}s stability window");

        main.Dispose();
        return alive;
    }

    private static void ReportNewHelpers(HashSet<string> seen)
    {
        foreach (var name in SteamProcessNames)
        {
            if (seen.Contains(name))
                continue;

            var processes = SafeGetProcesses(name);
            if (processes.Length == 0)
                continue;

            seen.Add(name);
            foreach (var process in processes)
            {
                Line($"{name} appeared: {DescribeProcess(process)}");
                process.Dispose();
            }
        }
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

    private static string SafeExitCode(Process process)
    {
        try { return process.ExitCode.ToString(); }
        catch { return "unknown"; }
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
    private const int TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

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
