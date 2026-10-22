using System.IO;
using GreenLuma_Manager.Models;

namespace GreenLuma_Manager.Services;

/// <summary>
/// Central rules for the system DLL proxy that GreenLumaRE loads from the
/// Steam folder. Keeping the filename logic here ensures deployment,
/// detection, enable/disable, and the settings UI always agree.
/// </summary>
public static class SystemProxyDll
{
    public const string DefaultName = "user32.dll";

    /// <summary>Proxy builds verified to ship with GreenLumaRE.</summary>
    public static readonly string[] BuiltInNames = ["user32.dll", "version.dll", "winmm.dll"];

    private const string User32SourceVariant = "user32SF.dll";
    private const string DisabledSuffix = "_DISABLED";
    private const int MaxNameLength = 100;

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    /// <summary>
    /// Names that must never be used as a proxy target. Deployment overwrites
    /// the destination file, so pointing it at a Steam or GreenLuma DLL would
    /// corrupt the installation.
    /// </summary>
    private static readonly string[] ProtectedNamePrefixes =
    [
        "greenluma", "applistmanager", "dllinjector",
        "steamclient", "steam", "tier0", "vstdlib",
        "gameoverlayrenderer", "crashhandler"
    ];

    /// <summary>
    /// Returns the canonical proxy filename. Missing or empty input resolves to
    /// <see cref="DefaultName"/> so legacy configs keep working.
    /// </summary>
    public static string Normalize(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return DefaultName;

        // Strip any path components so a malformed config value can never
        // escape the Steam directory.
        var fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? DefaultName : fileName.ToLowerInvariant();
    }

    /// <summary>
    /// Validates a user-supplied proxy filename without touching the filesystem.
    /// </summary>
    public static bool TryValidate(string? name, out string normalized, out string? error)
    {
        normalized = Normalize(name);
        error = null;

        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = "Enter a system DLL proxy filename.";
            return false;
        }

        if (trimmed.Length > MaxNameLength)
        {
            error = $"The filename is too long. Keep it under {MaxNameLength} characters.";
            return false;
        }

        if (trimmed.Contains(Path.DirectorySeparatorChar) ||
            trimmed.Contains(Path.AltDirectorySeparatorChar) ||
            trimmed.Contains(':'))
        {
            error = "Use a filename only, without a folder path.";
            return false;
        }

        if (!string.Equals(Path.GetExtension(trimmed), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            error = "The system DLL proxy must be a .dll file.";
            return false;
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "The filename contains characters that are not allowed.";
            return false;
        }

        var baseName = Path.GetFileNameWithoutExtension(trimmed);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            error = "Enter a filename before the .dll extension.";
            return false;
        }

        if (baseName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Filenames ending in {DisabledSuffix} are reserved for disabled proxies.";
            return false;
        }

        var upperName = baseName.TrimEnd('.', ' ').ToUpperInvariant();
        if (Array.IndexOf(ReservedDeviceNames, upperName) >= 0)
        {
            error = $"{trimmed} is a reserved Windows device name.";
            return false;
        }

        foreach (var prefix in ProtectedNamePrefixes)
        {
            if (upperName.StartsWith(prefix.ToUpperInvariant(), StringComparison.Ordinal))
            {
                error = $"{trimmed} is used by Steam or GreenLuma and cannot be overwritten.";
                return false;
            }
        }

        normalized = trimmed.ToLowerInvariant();
        return true;
    }

    public static string GetPath(string steamPath, string? name)
        => Path.Combine(steamPath, Normalize(name));

    public static string GetDisabledName(string? name)
    {
        var normalized = Normalize(name);
        return Path.GetFileNameWithoutExtension(normalized) + DisabledSuffix + Path.GetExtension(normalized);
    }

    public static string GetDisabledPath(string steamPath, string? name)
        => Path.Combine(steamPath, GetDisabledName(name));

    /// <summary>
    /// Returns true when this application is responsible for the proxy file.
    /// Proxies deployed by other tools stay unowned, so they are never renamed
    /// or overwritten without an explicit confirmation. A legacy install with
    /// no recorded deployment is treated as owned when it uses the historical
    /// user32.dll name.
    /// </summary>
    public static bool IsOwned(Config config, string? name)
    {
        var normalized = Normalize(name);
        var deployed = config.DeployedSystemProxyDll?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(deployed))
            return string.Equals(Normalize(deployed), normalized, StringComparison.OrdinalIgnoreCase);

        return string.Equals(normalized, DefaultName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the built-in proxy names currently present in the Steam folder
    /// in either the enabled or disabled form.
    /// </summary>
    public static string[] FindPresentProxies(string steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
            return [];

        var found = new List<string>();
        foreach (var name in BuiltInNames)
        {
            if (File.Exists(GetPath(steamPath, name)) ||
                File.Exists(GetDisabledPath(steamPath, name)))
            {
                found.Add(name);
            }
        }

        return [.. found];
    }

    /// <summary>
    /// Resolves the proxy binary inside the extracted GreenLumaRE archive.
    /// GreenLumaRE ships each proxy in its own folder (for example
    /// "System Proxy/version/version.dll"), so lookup is by exact filename
    /// anywhere in the tree. The SF variant is only used for user32.dll.
    /// </summary>
    public static string? FindArchiveSource(string sourceDir, User32DeployMode mode, string? proxyName)
    {
        var normalized = Normalize(proxyName);
        var sourceName = mode == User32DeployMode.User32SF && normalized == DefaultName
            ? User32SourceVariant
            : normalized;

        var match = FindArchiveFile(sourceDir, sourceName);

        // Legacy archives only contain user32SF.dll, which is renamed to
        // user32.dll. Never use it as a source for a different proxy name.
        if (match == null &&
            normalized == DefaultName &&
            !string.Equals(sourceName, User32SourceVariant, StringComparison.OrdinalIgnoreCase))
        {
            match = FindArchiveFile(sourceDir, User32SourceVariant);
        }

        return match;
    }

    private static string? FindArchiveFile(string sourceDir, string fileName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                return null;

            return Directory
                .EnumerateFiles(sourceDir, fileName, SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => GetArchiveScore(path, fileName))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to locate '{fileName}' in the extracted archive");
            return null;
        }
    }

    /// <summary>
    /// Prefers the documented layout ("System Proxy/&lt;name&gt;/&lt;name&gt;.dll")
    /// while remaining tolerant of other folder names and of future packaging
    /// changes.
    /// </summary>
    private static int GetArchiveScore(string path, string fileName)
    {
        var directory = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.Equals(directory, baseName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(directory, fileName, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var parent = Path.GetFileName(
            Path.GetDirectoryName(Path.GetDirectoryName(path) ?? string.Empty) ?? string.Empty);
        if (string.Equals(parent, "System Proxy", StringComparison.OrdinalIgnoreCase))
            return 1;

        if (path.Contains("StealthMode", StringComparison.OrdinalIgnoreCase))
            return 2;

        return 3;
    }
}
