using System.IO;
using System.Text.RegularExpressions;
using GreenLuma_Manager.Services;
using Microsoft.Win32;

namespace GreenLuma_Manager.Utilities;

public partial class PathDetector
{
    /// <summary>Checks whether a path refers to an existing directory.</summary>
    public static bool IsValidDirectory(string? path)
        => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    /// <summary>Creates a directory if it does not already exist.</summary>
    public static void EnsureExists(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }

    [GeneratedRegex(@"GreenLuma_(\d{4})_x(64|86)\.dll", RegexOptions.IgnoreCase)]
    public static partial Regex GreenLumaDll();

    // ── Application data directories ──────────────────────────────────────────

    /// <summary>Base directory for all app data (%LOCALAPPDATA%\GLM_Manager).</summary>
    public static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GLM_Manager");

    public static string ConfigDir => AppDataDir;
    public static string ProfilesDir => Path.Combine(AppDataDir, "profiles");
    public static string PluginsDir => Path.Combine(AppDataDir, "plugins");
    public static string IconsDir => Path.Combine(AppDataDir, "icons");
    public static string WebView2Dir => Path.Combine(AppDataDir, "WebView2");

    // ── Steam / GreenLuma path detection ──────────────────────────────────────

    public static (string SteamPath, string GreenLumaPath) DetectPaths()
    {
        Logger.Debug("Detecting Steam and GreenLuma paths");
        var steamPath = DetectSteamPath();
        var greenLumaPath = DetectGreenLumaPath(steamPath);

        if (!string.IsNullOrEmpty(steamPath))
            Logger.Info($"Steam path detected: '{steamPath}'");
        if (!string.IsNullOrEmpty(greenLumaPath))
            Logger.Info($"GreenLuma path detected: '{greenLumaPath}'");

        return (steamPath, greenLumaPath);
    }

    public static string DetectSteamPath()
    {
        Logger.Debug("Detecting Steam path");
        var registryPath = TryDetectSteamFromRegistry();
        if (!string.IsNullOrEmpty(registryPath))
            return registryPath;

        return TryDetectSteamFromCommonLocations();
    }

    private static string TryDetectSteamFromRegistry()
    {
        Logger.Debug("Trying to detect Steam from registry");

        var path = TryGetRegistryPath("SOFTWARE\\WOW6432Node\\Valve\\Steam");
        if (!string.IsNullOrEmpty(path))
        {
            Logger.Debug($"Steam found in registry: '{path}'");
            return path;
        }

        path = TryGetRegistryPath("SOFTWARE\\Valve\\Steam");
        if (!string.IsNullOrEmpty(path))
        {
            Logger.Debug($"Steam found in registry: '{path}'");
            return path;
        }

        Logger.Warn("Steam not found in registry");
        return string.Empty;
    }

    private static string? TryGetRegistryPath(string keyPath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            var installPath = key?.GetValue("InstallPath") as string;

            if (!string.IsNullOrWhiteSpace(installPath) &&
                File.Exists(Path.Combine(installPath, "Steam.exe")))
                return installPath;
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static string TryDetectSteamFromCommonLocations()
    {
        Logger.Debug("Trying to detect Steam from common file system locations");
        string[] commonPaths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Steam")
        ];

        foreach (var path in commonPaths)
            if (File.Exists(Path.Combine(path, "Steam.exe")))
            {
                Logger.Debug($"Steam found at '{path}'");
                return path;
            }

        Logger.Debug("Steam not found in common locations");
        return string.Empty;
    }

    public static string DetectGreenLumaPath(string steamPath)
    {
        Logger.Debug("Detecting GreenLuma path");
        if (IsValidDirectory(steamPath)
            && ContainsGreenLumaFiles(steamPath))
        {
            Logger.Debug($"GreenLuma found in Steam directory: '{steamPath}'");
            return steamPath;
        }

        return TryDetectGreenLumaFromCommonLocations();
    }

    private static string TryDetectGreenLumaFromCommonLocations()
    {
        string[] commonPaths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GreenLuma"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "GreenLuma"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "GreenLuma"),
            "C:\\GreenLuma",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GreenLuma")
        ];

        foreach (var path in commonPaths)
            if (Directory.Exists(path) && ContainsGreenLumaFiles(path))
                return path;

        return string.Empty;
    }

    private static bool ContainsGreenLumaFiles(string directory)
    {
        if (!File.Exists(Path.Combine(directory, "DLLInjector.exe")))
            return false;

        try
        {
            var files = Directory.GetFiles(directory, "GreenLuma_*_x*.dll");
            foreach (var file in files)
            {
                if (GreenLumaDll().IsMatch(Path.GetFileName(file)))
                    return true;
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }
}