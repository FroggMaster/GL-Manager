using System.IO;
using System.Runtime.CompilerServices;
using GreenLuma_Manager.Utilities;

namespace GreenLuma_Manager.Services;

/// <summary>
/// Centralized logging service. Writes to <c>%LOCALAPPDATA%\GLM_Manager\logs\GreenLuma-Manager.log</c>.
/// All log entries include timestamp, level, source file, member name, line number, and message.
/// Logging never throws, all I/O failures are silently handled.
/// </summary>
public static class Logger
{
    private static readonly string LogDir = Path.Combine(PathDetector.AppDataDir, "logs");
    private static readonly string LogPath = Path.Combine(LogDir, "GreenLuma-Manager.log");
    private static readonly string PrevLogPath = Path.Combine(LogDir, "GreenLuma-Manager.prev.log");
    private static readonly object Lock = new();

    static Logger()
    {
        try
        {
            PathDetector.EnsureExists(LogDir);

            // Older versions wrote these logs to the app data root.
            MigrateLegacyLog("GreenLuma-Manager.log");
            MigrateLegacyLog("GreenLuma-Manager.prev.log");

            // Rotate: rename previous session's log to .prev, start fresh
            if (File.Exists(LogPath))
            {
                File.Move(LogPath, PrevLogPath, overwrite: true);
            }
        }
        catch
        {
            // Logging must never crash the application
        }
    }

    /// <summary>
    /// Moves a log written by an older version from the app data root into the
    /// logs folder. Existing destination files are left untouched.
    /// </summary>
    private static void MigrateLegacyLog(string fileName)
    {
        try
        {
            var legacyPath = Path.Combine(PathDetector.AppDataDir, fileName);
            var destinationPath = Path.Combine(LogDir, fileName);

            if (File.Exists(legacyPath) && !File.Exists(destinationPath))
                File.Move(legacyPath, destinationPath);
        }
        catch
        {
            // Best effort only; logging must never throw.
        }
    }

    // ── Public API ────────────────────────────────────────────────────

    public static void Info(
        string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Write("Info", message, memberName, filePath, lineNumber);
    }

    public static void Debug(
        string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Write("Debug", message, memberName, filePath, lineNumber);
    }

    public static void Warn(
        string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Write("Warn", message, memberName, filePath, lineNumber);
    }

    public static void Error(
        string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Write("Error", message, memberName, filePath, lineNumber);
    }

    public static void Error(
        Exception ex,
        string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Write("Error", $"{message}: {ex.GetType().Name} — {ex.Message}", memberName, filePath, lineNumber);
    }

    // ── Internal ──────────────────────────────────────────────────────

    private static void Write(string level, string message, string memberName, string filePath, int lineNumber)
    {
        try
        {
            var fileName = Path.GetFileName(filePath);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var entry = $"[{timestamp}] [{level}] [{fileName}:{memberName}:{lineNumber}] {message}";

            lock (Lock)
            {
                File.AppendAllText(LogPath, entry + Environment.NewLine);
            }
        }
        catch
        {
            // Silent — logging must never crash the app
        }
    }
}
