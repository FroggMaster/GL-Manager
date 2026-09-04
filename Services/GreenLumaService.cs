using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using GreenLuma_Manager.Dialogs;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Utilities;

namespace GreenLuma_Manager.Services;

public partial class GreenLumaService
{
    private const int ProcessKillTimeoutMs = 5000;
    public const int AppListLimit = 135;
    private static readonly string[] SteamProcessNames = ["steam", "steamwebhelper", "steamerrorfilereporter"];

    [GeneratedRegex(@"[A-Za-z]:\\[^""\r\n]+?\.dll", RegexOptions.IgnoreCase)]
    private static partial Regex DllPathRegex();

    private static (string? Year, string? Arch, string? DllPath) FindInstalledDll(string path)
    {
        string? year = null;
        string? arch = null;
        string? dllPath = null;
        try
        {
            var dllFiles = Directory.GetFiles(path, "GreenLuma_*_x*.dll");
            foreach (var file in dllFiles)
            {
                var match = PathDetector.GreenLumaDll().Match(Path.GetFileName(file));
                if (match.Success)
                {
                    year = match.Groups[1].Value;
                    arch = match.Groups[2].Value;
                    dllPath = file;
                    if (string.Equals(arch, "64", StringComparison.OrdinalIgnoreCase))
                        break;
                }
            }
        }
        catch
        {
            // ignored
        }
        return (year, arch, dllPath);
    }

    public static (Version? FileVersion, string? DllPath) DetectInstalledVersion(string greenLumaPath)
    {
        if (!PathDetector.IsValidDirectory(greenLumaPath))
            return (null, null);

        var (_, _, dllPath) = FindInstalledDll(greenLumaPath);

        if (dllPath == null)
            return (null, null);

        Version? fileVersion = null;
        try
        {
            var versionStr = FileVersionInfo.GetVersionInfo(dllPath).FileVersion;
            if (versionStr is not null && Version.TryParse(versionStr, out var fullVersion))
            {
                // Strip revision so "1.7.3.0" displays as "1.7.3"
                fileVersion = new Version(fullVersion.Major, fullVersion.Minor, Math.Max(0, fullVersion.Build));
            }
        }
        catch
        {
            // ignored
        }

        return (fileVersion, dllPath);
    }

    /// <summary>
    /// Detects the active GreenLuma installation method.
    /// Always checks every path (no early returns) so callers can rely on it
    /// being a complete picture.  When <paramref name="preferredMode"/> is
    /// non-empty and matches an available installation it is returned;
    /// otherwise the default priority (User32 → Normal → StealthAny → None)
    /// is used.
    /// </summary>
    public static GreenLumaInstallMethod DetectInstallMethod(
        string? steamPath,
        string? greenLumaPath,
        string? preferredMode = null)
    {
        // Collect every mode present on disk.
        // StealthAny is only counted when the GL path is distinct from the
        // Steam directory — the files inside SteamDir are already covered
        // by the Normal check and must not be double-counted.
        var hasUser32 = !string.IsNullOrWhiteSpace(steamPath) &&
                         File.Exists(Path.Combine(steamPath, "user32.dll"));
        var hasNormal = !string.IsNullOrWhiteSpace(steamPath) &&
                         File.Exists(Path.Combine(steamPath, "DLLInjector.exe")) &&
                         HasGreenLumaDll(steamPath);
        var hasStealthAny = HasStealthAnyFiles(greenLumaPath) &&
                            PathsAreDistinct(greenLumaPath, steamPath);

        var count = (hasUser32 ? 1 : 0) + (hasNormal ? 1 : 0) + (hasStealthAny ? 1 : 0);
        if (count == 0)
            return GreenLumaInstallMethod.None;

        // Only one mode → unambiguous
        if (count == 1)
        {
            if (hasUser32) return GreenLumaInstallMethod.User32;
            if (hasNormal) return GreenLumaInstallMethod.Normal;
            return GreenLumaInstallMethod.StealthAny;
        }

        // Multiple modes — honour saved preference when it still exists
        if (!string.IsNullOrWhiteSpace(preferredMode))
        {
            if (hasUser32 && preferredMode.Equals(GreenLumaInstallMethod.User32.ToString(), StringComparison.OrdinalIgnoreCase))
                return GreenLumaInstallMethod.User32;
            if (hasNormal && preferredMode.Equals(GreenLumaInstallMethod.Normal.ToString(), StringComparison.OrdinalIgnoreCase))
                return GreenLumaInstallMethod.Normal;
            if (hasStealthAny && preferredMode.Equals(GreenLumaInstallMethod.StealthAny.ToString(), StringComparison.OrdinalIgnoreCase))
                return GreenLumaInstallMethod.StealthAny;
        }

        // Default priority
        if (hasUser32) return GreenLumaInstallMethod.User32;
        // When the user has configured a distinct GreenLuma directory with
        // valid StealthAny files, prefer StealthAny over Normal — Normal
        // files in SteamPath are often leftovers from a previous install.
        if (hasStealthAny) return GreenLumaInstallMethod.StealthAny;
        if (hasNormal) return GreenLumaInstallMethod.Normal;
        return GreenLumaInstallMethod.None;
    }

    /// <summary>
    /// Returns true when the given path contains at least one GreenLuma_*_x*.dll.
    /// </summary>
    public static bool HasGreenLumaDll(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "GreenLuma_*_x*.dll").Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when the given path contains a StealthAny-ready installation
    /// (DLLInjector.exe + GreenLuma DLL).
    /// </summary>
    public static bool HasStealthAnyFiles(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        return File.Exists(Path.Combine(path, "DLLInjector.exe")) && HasGreenLumaDll(path);
    }

    /// <summary>
    /// Resolves the effective GreenLuma path, falling back to
    /// <paramref name="lastStealthAnyPath"/> when <paramref name="greenLumaPath"/>
    /// does not point to a distinct StealthAny installation.
    /// This ensures a separate StealthAny folder is never missed when
    /// <paramref name="greenLumaPath"/> equals the Steam directory (e.g. after a
    /// Normal or User32 detection overwrites the saved path).
    /// </summary>
    public static string ResolveGreenLumaPath(
        string? greenLumaPath,
        string? steamPath,
        string? lastStealthAnyPath)
    {
        if (!HasStealthAnyFiles(greenLumaPath) || !PathsAreDistinct(greenLumaPath, steamPath))
        {
            var last = lastStealthAnyPath?.Trim();
            if (!string.IsNullOrWhiteSpace(last) &&
                PathsAreDistinct(last, steamPath) &&
                HasStealthAnyFiles(last))
            {
                return last;
            }
        }

        return greenLumaPath ?? string.Empty;
    }

    /// <summary>
    /// Returns false when either path is null/empty, or both paths point
    /// to the same directory (by full path).  StealthAny must be at a
    /// location distinct from the Steam folder.
    /// </summary>
    internal static bool PathsAreDistinct(string? pathA, string? pathB)
    {
        if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
            return false;

        try
        {
            return !string.Equals(
                Path.GetFullPath(pathA).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(pathB).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Renames user32.dll → user32_DISABLED.dll in the Steam directory,
    /// preventing auto-injection so a DLLInjector-based setup can be used instead.
    /// </summary>
    public static void DisableUser32(string steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath))
            return;

        var user32Path = Path.Combine(steamPath, "user32.dll");
        var disabledPath = Path.Combine(steamPath, "user32_DISABLED.dll");

        if (File.Exists(user32Path))
        {
            File.Move(user32Path, disabledPath, overwrite: true);
            Logger.Debug($"Disabled user32: {user32Path} → {disabledPath}");
        }
    }

    /// <summary>
    /// Reverses <see cref="DisableUser32"/> — renames user32_DISABLED.dll →
    /// user32.dll in the Steam directory, re-enabling auto-injection.
    /// </summary>
    public static void EnableUser32(string steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath))
            return;

        var disabledPath = Path.Combine(steamPath, "user32_DISABLED.dll");
        var user32Path = Path.Combine(steamPath, "user32.dll");

        if (File.Exists(disabledPath))
        {
            File.Move(disabledPath, user32Path, overwrite: true);
            Logger.Debug($"Enabled user32: {disabledPath} → {user32Path}");
        }
    }

    /// <summary>
    /// Checks for mixed GreenLuma installations on disk and prompts the user to
    /// resolve conflicts (e.g. User32 + Normal, User32 + StealthAny, Normal + StealthAny).
    /// Updates <paramref name="config"/>.<see cref="Config.PreferredMode"/> and saves
    /// the config when a choice is made.
    /// </summary>
    /// <param name="persisted">True when the choice was saved to disk.</param>
    /// <returns>True when a mixed state was detected and the user was prompted.</returns>
    public static bool CheckMixedInstallState(
        string? steamPath,
        string? greenLumaPath,
        Config config,
        out bool persisted,
        bool persistPreference = true)
    {
        persisted = false;

        // Resolve the effective GL path in case GreenLumaPath points to the
        // Steam directory (Normal/User32 mode) and the StealthAny install
        // lives in a separate folder tracked via LastStealthAnyPath.
        greenLumaPath = ResolveGreenLumaPath(greenLumaPath, steamPath, config.LastStealthAnyPath);

        var hasUser32 = !string.IsNullOrWhiteSpace(steamPath) &&
                         File.Exists(Path.Combine(steamPath, "user32.dll"));
        var hasNormal = !string.IsNullOrWhiteSpace(steamPath) &&
                         File.Exists(Path.Combine(steamPath, "DLLInjector.exe")) &&
                         HasGreenLumaDll(steamPath);
        var hasStealthAny = HasStealthAnyFiles(greenLumaPath) &&
                            PathsAreDistinct(greenLumaPath, steamPath);

        var count = (hasUser32 ? 1 : 0) + (hasNormal ? 1 : 0) + (hasStealthAny ? 1 : 0);
        if (count < 2)
            return false;

        // When the user has explicitly selected a mode (PreferredMode is set),
        // only interrupt if User32 is involved but NOT preferred.
        // Normal + StealthAny coexist without conflict.
        // User32 auto-injects, so changing away from it requires a prompt,
        // but if User32 is already the preferred mode, nothing to resolve.
        if (!string.IsNullOrWhiteSpace(config.PreferredMode))
        {
            if (!hasUser32)
                return false;
            if (string.Equals(config.PreferredMode,
                    GreenLumaInstallMethod.User32.ToString(),
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Local helper: shows the checkbox when the caller requests
        // session-only prompting (persistPreference == false).
        int ShowPrompt(string message, string title, string[] buttons, out bool remember)
        {
            if (persistPreference)
            {
                remember = false;
                return CustomMessageBox.Show(message, title, buttons);
            }

            var (index, isChecked) = CustomMessageBox.ShowWithRemember(message, title, buttons);
            remember = isChecked;
            return index;
        }

        // ── User32 + Normal + StealthAny ───────────────────────────
        if (hasUser32 && hasNormal && hasStealthAny)
        {
            var choice = ShowPrompt(
                "User32, Normal, and StealthAny mode installations were detected.\n\n" +
                "If you choose Normal or StealthAny mode, User32 mode will be disabled.\n\n" +
                "Which mode would you like to use?",
                "Mixed Installation Detected",
                ["Normal", "User32", "StealthAny"],
                out var remember3);

            switch (choice)
            {
                case 0: // Normal
                    DisableUser32(steamPath!);
                    config.PreferredMode = GreenLumaInstallMethod.Normal.ToString();
                    break;
                case 1: // User32
                    config.PreferredMode = GreenLumaInstallMethod.User32.ToString();
                    break;
                default: // StealthAny
                    DisableUser32(steamPath!);
                    config.PreferredMode = GreenLumaInstallMethod.StealthAny.ToString();
                    break;
            }

            if (persistPreference || remember3)
            {
                ConfigService.Save(config);
                persisted = true;
            }
            return true;
        }

        // ── User32 + Normal ────────────────────────────────────────
        if (hasUser32 && hasNormal)
        {
            var choice = ShowPrompt(
                "Both User32 and Normal mode installations were detected.\n\n" +
                "User32 takes priority because it auto-injects into Steam.\n\n" +
                "If you choose Normal mode User32 mode will be disabled.\n\n" +
                "Which mode would you like to use?",
                "Mixed Installation Detected",
                ["Normal", "User32"],
                out var rememberUN);

            if (choice == 0) // Normal
            {
                DisableUser32(steamPath!);
                config.PreferredMode = GreenLumaInstallMethod.Normal.ToString();
            }
            else
            {
                config.PreferredMode = GreenLumaInstallMethod.User32.ToString();
            }

            if (persistPreference || rememberUN)
            {
                ConfigService.Save(config);
                persisted = true;
            }
            return true;
        }

        // ── User32 + StealthAny ────────────────────────────────────
        if (hasUser32 && hasStealthAny)
        {
            var choice = ShowPrompt(
                "Both User32 and StealthAny mode installations were detected.\n\n" +
                "User32 takes priority because it auto-injects into Steam.\n\n" +
                "If you choose StealthAny mode User32 mode will be disabled.\n\n" +
                "Which mode would you like to use?",
                "Mixed Installation Detected",
                ["StealthAny", "User32"],
                out var rememberUS);

            if (choice == 0) // StealthAny
            {
                DisableUser32(steamPath!);
                config.PreferredMode = GreenLumaInstallMethod.StealthAny.ToString();
            }
            else
            {
                config.PreferredMode = GreenLumaInstallMethod.User32.ToString();
            }

            if (persistPreference || rememberUS)
            {
                ConfigService.Save(config);
                persisted = true;
            }
            return true;
        }

        // ── Normal + StealthAny ────────────────────────────────────
        if (hasNormal && hasStealthAny)
        {
            var choice = ShowPrompt(
                "Both Normal and StealthAny mode installations were detected.\n\n" +
                "Both use DLLInjector.exe and do not conflict.\n\n" +
                "Which mode would you like to use?",
                "Mixed Installation Detected",
                ["Normal", "StealthAny"],
                out var rememberNS);

            config.PreferredMode = choice == 1
                ? GreenLumaInstallMethod.StealthAny.ToString()
                : GreenLumaInstallMethod.Normal.ToString();

            if (persistPreference || rememberNS)
            {
                ConfigService.Save(config);
                persisted = true;
            }
            return true;
        }

        return false;
    }

    public static (bool IsValid, bool IsStealthOnly, List<string> MissingFiles) ValidateInstallation(string path)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return (false, false, missing);

        var (year, arch, _) = FindInstalledDll(path);

        if (year == null || arch == null)
        {
            missing.Add("GreenLuma_YYYY_xNN.dll (e.g. GreenLuma_2025_x64.dll)");
            return (false, false, missing);
        }

        var primaryDll = $"GreenLuma_{year}_x{arch}.dll";

        var stealthFiles = new List<string>
        {
            "DLLInjector.exe",
            "DLLInjector.ini",
            $"GreenLumaSettings_{year}.exe",
            primaryDll
        };

        var otherArch = arch == "64" ? "86" : "64";
        var fullFiles = new List<string>
        {
            $"GreenLuma_{year}_x{otherArch}.dll",
            Path.Combine($"GreenLuma{year}_Files", "AchievementUnlocked.wav"),
            Path.Combine($"GreenLuma{year}_Files", "BootImage.bmp")
        };

        var missingStealth = new List<string>();
        foreach (var f in stealthFiles)
            if (!File.Exists(Path.Combine(path, f)))
                missingStealth.Add(f);

        if (missingStealth.Count > 0)
        {
            return (false, false, missingStealth);
        }

        var missingFull = new List<string>();
        foreach (var f in fullFiles)
            if (!File.Exists(Path.Combine(path, f)))
                missingFull.Add(f);

        var x86Launcher = Path.Combine(path, "bin", "x86launcher.exe");
        var x64Launcher = Path.Combine(path, "bin", "x64launcher.exe");
        if (!File.Exists(x86Launcher) && !File.Exists(x64Launcher))
            missingFull.Add(Path.Combine("bin", "x86launcher.exe"));

        if (missingFull.Count > 0)
        {
            return (true, true, missingFull);
        }

        return (true, false, new List<string>());
    }
    public static bool IsAppListGenerated(Config config)
    {
        if (string.IsNullOrWhiteSpace(config.GreenLumaPath))
            return false;

        var appListPath = Path.Combine(config.GreenLumaPath, "AppList");

        return Directory.Exists(appListPath) &&
               Directory.GetFiles(appListPath, "*.txt").Length > 0;
    }

    public static async Task<int> GenerateAppListAsync(Profile? profile, Config? config)
    {
        if (profile == null || config == null || string.IsNullOrWhiteSpace(config.GreenLumaPath))
            return -1;

        try
        {
            var appListPath = Path.Combine(config.GreenLumaPath, "AppList");
            Directory.CreateDirectory(appListPath);

            foreach (var file in Directory.GetFiles(appListPath, "*.txt"))
                File.Delete(file);

            var allAppIds = new List<string>();

            foreach (var game in profile.Games)
            {
                allAppIds.Add(game.AppId);
                allAppIds.AddRange(game.Depots);
            }

            var totalCount = allAppIds.Count;

            var limitedAppIds = allAppIds.Take(AppListLimit).ToList();

            for (var i = 0; i < limitedAppIds.Count; i++)
            {
                var filePath = Path.Combine(appListPath, $"{i}.txt");
                await File.WriteAllTextAsync(filePath, limitedAppIds[i]);
            }

            return totalCount;
        }
        catch
        {
            return -1;
        }
    }

    public static async Task<bool> LaunchGreenLumaAsync(Config config)
    {
        return await Task.Run(() =>
        {
            try
            {
                Logger.Info("CLI launch started");

                if (!ValidatePaths(config))
                    return false;

                KillSteam(config);

                return LaunchInjector(config);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unhandled exception in CLI launch");
                return false;
            }
        });
    }

    private static bool ValidatePaths(Config config)
    {
        if (string.IsNullOrWhiteSpace(config.SteamPath) ||
            string.IsNullOrWhiteSpace(config.GreenLumaPath))
        {
            Logger.Warn($"CLI path validation failed: SteamPath or GreenLumaPath is empty (SteamPath='{config.SteamPath}' GreenLumaPath='{config.GreenLumaPath}')");
            return false;
        }

        var steamExePath = Path.Combine(config.SteamPath, "Steam.exe");
        var injectorPath = Path.Combine(config.GreenLumaPath, "DLLInjector.exe");

        var steamExists = File.Exists(steamExePath);
        var injectorExists = File.Exists(injectorPath);

        if (!steamExists || !injectorExists)
        {
            Logger.Warn($"CLI path validation failed: Steam.exe exists={steamExists} DLLInjector.exe exists={injectorExists}");
        }

        return steamExists && injectorExists;
    }

    private static bool LaunchInjector(Config config)
    {
        var injectorPath = Path.Combine(config.GreenLumaPath, "DLLInjector.exe");

        if (!File.Exists(injectorPath))
        {
            Logger.Error($"CLI launch: DLLInjector.exe not found at '{injectorPath}'");
            return false;
        }

        Logger.Info("CLI launch: updating DLLInjector.ini");
        UpdateInjectorIni(config);

        Logger.Info($"CLI launch: starting DLLInjector.exe from '{config.GreenLumaPath}'");
        Process.Start(new ProcessStartInfo
        {
            FileName = injectorPath,
            WorkingDirectory = config.GreenLumaPath,
            UseShellExecute = true
        });

        return true;
    }

    internal static void KillSteam(Config config)
    {
        try
        {
            var steamExePath = Path.Combine(config.SteamPath, "Steam.exe");

            if (File.Exists(steamExePath))
                try
                {
                    Logger.Info("Sending Steam -shutdown for graceful exit");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = steamExePath,
                        Arguments = "-shutdown",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    Thread.Sleep(2000);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Steam -shutdown failed: {ex.Message}");
                }

            foreach (var processName in SteamProcessNames) KillProcessesByName(processName);
        }
        catch (Exception ex)
        {
            Logger.Warn($"KillSteam encountered an error: {ex.Message}");
        }
    }

    private static void KillProcessesByName(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
            try
            {
                Logger.Debug($"Force-killing {processName} process (PID {process.Id})");
                process.Kill();
                process.WaitForExit(ProcessKillTimeoutMs);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Failed to kill {processName} (PID {process.Id}): {ex.Message}");
            }
    }

    private static bool AreSameDirectory(string path1, string path2)
    {
        try
        {
            var fullPath1 = Path.GetFullPath(path1)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath2 = Path.GetFullPath(path2)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullPath1, fullPath2, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // ignored
            return false;
        }
    }

    internal static void UpdateInjectorIni(Config config)
    {
        UpdateInjectorIni(config, config.GreenLumaPath);
    }

    internal static void UpdateInjectorIni(Config config, string basePath)
    {
        try
        {
            var iniPath = Path.Combine(basePath, "DLLInjector.ini");

            if (!File.Exists(iniPath))
            {
                Logger.Debug($"DLLInjector.ini not found at '{iniPath}', skipping update");
                return;
            }

            var lines = File.ReadAllLines(iniPath).ToList();
            var dllValue = ExtractDllValue(lines);
            var settings = BuildInjectorSettings(config, dllValue);
            var updatedLines = ApplySettings(lines, settings);

            File.WriteAllLines(iniPath, updatedLines);
            Logger.Debug($"Updated DLLInjector.ini at '{iniPath}' with {settings.Count} setting(s)");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to update DLLInjector.ini: {ex.Message}");
        }
    }

    private static string? ExtractDllValue(List<string> lines)
    {
        try
        {
            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("Dll", StringComparison.OrdinalIgnoreCase))
                {
                    var equalsIndex = line.IndexOf('=');
                    if (equalsIndex >= 0 && equalsIndex < line.Length - 1)
                    {
                        var raw = line[(equalsIndex + 1)..].Trim();
                        var cleaned = CleanDllValue(raw);
                        return cleaned;
                    }

                    break;
                }
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static string CleanDllValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var s = raw.Trim();
        s = s.Trim('"', '\'', ' ');

        try
        {
            var m = DllPathRegex().Match(s);

            if (m.Success) return m.Value;
        }
        catch
        {
            // ignored
        }

        return s;
    }

    private static Dictionary<string, string> BuildInjectorSettings(Config config, string? dllValue)
    {
        var useSeparatePaths = !AreSameDirectory(config.SteamPath, config.GreenLumaPath) ||
                               (!string.IsNullOrWhiteSpace(dllValue) && Path.IsPathRooted(dllValue));

        var steamExePath = Path.Combine(config.SteamPath, "Steam.exe");

        var settings = new Dictionary<string, string>
        {
            ["FileToCreate_1"] = " NoQuestion.bin"
        };

        if (useSeparatePaths)
        {
            settings["UseFullPathsFromIni"] = " 1";
            settings["Exe"] = $" \"{steamExePath}\"";

            if (!string.IsNullOrWhiteSpace(dllValue))
            {
                var candidate = dllValue.Trim();

                bool rooted;
                try
                {
                    rooted = Path.IsPathRooted(candidate);
                }
                catch
                {
                    // ignored
                    rooted = false;
                }

                if (rooted)
                {
                    var full = candidate;
                    try
                    {
                        full = Path.GetFullPath(candidate);
                    }
                    catch
                    {
                        // ignored
                    }

                    settings["Dll"] = $" \"{full}\"";
                }
                else
                {
                    var fullDllPath = Path.Combine(config.GreenLumaPath, candidate);
                    try
                    {
                        fullDllPath = Path.GetFullPath(fullDllPath);
                    }
                    catch
                    {
                        // ignored
                    }

                    settings["Dll"] = $" \"{fullDllPath}\"";
                }
            }
        }
        else
        {
            settings["UseFullPathsFromIni"] = " 0";
            settings["Exe"] = " Steam.exe";

            if (!string.IsNullOrWhiteSpace(dllValue)) settings["Dll"] = $" {dllValue}";
        }

        if (config.NoHook)
            ApplyStealthModeSettings(settings);
        else
            ApplyNormalModeSettings(settings);

        return settings;
    }

    private static void ApplyStealthModeSettings(Dictionary<string, string> settings)
    {
        settings["CommandLine"] = "";
        settings["WaitForProcessTermination"] = " 0";
        settings["EnableFakeParentProcess"] = " 1";
        settings["EnableMitigationsOnChildProcess"] = " 0";
        settings["CreateFiles"] = " 2";
        settings["FileToCreate_2"] = " StealthMode.bin";
    }

    private static void ApplyNormalModeSettings(Dictionary<string, string> settings)
    {
        settings["CommandLine"] = " -inhibitbootstrap";
        settings["WaitForProcessTermination"] = " 1";
        settings["EnableFakeParentProcess"] = " 0";
        settings["CreateFiles"] = " 1";
        settings.TryAdd("FileToCreate_2", "");
    }

    private static List<string> ApplySettings(List<string> originalLines, Dictionary<string, string> settings)
    {
        var result = new List<string>();

        foreach (var line in originalLines)
        {
            var trimmed = line.Trim();
            var matched = false;

            if (!string.IsNullOrWhiteSpace(trimmed) && trimmed[0] != '#' && trimmed.Contains('='))
            {
                var equalsIndex = trimmed.IndexOf('=');
                var key = trimmed[..equalsIndex].Trim();

                foreach (var setting in settings)
                    if (string.Equals(key, setting.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add($"{setting.Key}={setting.Value}");
                        matched = true;
                        break;
                    }
            }

            if (!matched) result.Add(line);
        }

        return result;
    }
}