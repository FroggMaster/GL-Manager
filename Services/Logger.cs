using System.IO;
using System.Runtime.CompilerServices;
using GreenLuma_Manager.Utilities;

namespace GreenLuma_Manager.Services;

/// <summary>
/// Centralized logging service. Writes to <c>%LOCALAPPDATA%\GLM_Manager\GreenLuma-Manager.log</c>.
/// All log entries include timestamp, level, source file, member name, line number, and message.
/// Logging never throws — all I/O failures are silently handled.
/// </summary>
public static class Logger
{
    private static readonly string LogPath = Path.Combine(PathDetector.AppDataDir, "GreenLuma-Manager.log");
    private static readonly string PrevLogPath = Path.Combine(PathDetector.AppDataDir, "GreenLuma-Manager.prev.log");
    private static readonly object Lock = new();

    static Logger()
    {
        try
        {
            PathDetector.EnsureExists(PathDetector.AppDataDir);

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
