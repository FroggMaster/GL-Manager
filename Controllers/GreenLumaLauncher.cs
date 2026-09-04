using System.Diagnostics;
using System.IO;
using System.Threading;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;

namespace GreenLuma_Manager.Controllers;

public class GreenLumaLauncher
{
    public bool ValidatePaths(Config config)
    {
        var method = GreenLumaService.DetectInstallMethod(
            config.SteamPath, config.GreenLumaPath, config.PreferredMode);

        Logger.Debug($"Validating paths for method={method} SteamPath='{config.SteamPath}' GreenLumaPath='{config.GreenLumaPath}'");

        var valid = method switch
        {
            GreenLumaInstallMethod.Normal =>
                !string.IsNullOrWhiteSpace(config.SteamPath) &&
                File.Exists(Path.Combine(config.SteamPath, "DLLInjector.exe")) &&
                GreenLumaService.HasGreenLumaDll(config.SteamPath),

            GreenLumaInstallMethod.StealthAny =>
                !string.IsNullOrWhiteSpace(config.GreenLumaPath) &&
                File.Exists(Path.Combine(config.GreenLumaPath, "DLLInjector.exe")) &&
                GreenLumaService.HasGreenLumaDll(config.GreenLumaPath),

            GreenLumaInstallMethod.User32 =>
                !string.IsNullOrWhiteSpace(config.SteamPath) &&
                File.Exists(Path.Combine(config.SteamPath, "user32.dll")),

            _ => false
        };

        if (!valid)
        {
            var missing = method switch
            {
                GreenLumaInstallMethod.Normal =>
                    (!File.Exists(Path.Combine(config.SteamPath, "DLLInjector.exe"))
                        ? "DLLInjector.exe not found in SteamPath; " : "") +
                    (!GreenLumaService.HasGreenLumaDll(config.SteamPath)
                        ? "No GreenLuma DLL in SteamPath" : ""),
                GreenLumaInstallMethod.StealthAny =>
                    (!File.Exists(Path.Combine(config.GreenLumaPath, "DLLInjector.exe"))
                        ? "DLLInjector.exe not found in GreenLumaPath; " : "") +
                    (!GreenLumaService.HasGreenLumaDll(config.GreenLumaPath)
                        ? "No GreenLuma DLL in GreenLumaPath" : ""),
                GreenLumaInstallMethod.User32 =>
                    !File.Exists(Path.Combine(config.SteamPath, "user32.dll"))
                        ? "user32.dll not found in SteamPath" : "",
                _ => "No GreenLuma installation detected"
            };
            Logger.Warn($"Path validation failed: {missing}");
        }

        return valid;
    }

    public async Task<bool> LaunchAsync(Config config)
    {
        return await Task.Run(() =>
        {
            try
            {
                var method = GreenLumaService.DetectInstallMethod(
                    config.SteamPath, config.GreenLumaPath, config.PreferredMode);

                Logger.Info($"Launch started — detected method: {method}");

                if (method == GreenLumaInstallMethod.None)
                {
                    Logger.Error("No GreenLuma installation detected, aborting launch");
                    return false;
                }

                if (!ValidatePaths(config))
                {
                    Logger.Error("Path validation failed, aborting launch");
                    return false;
                }

                Logger.Info("Killing Steam processes...");
                GreenLumaService.KillSteam(config);

                var result = method switch
                {
                    GreenLumaInstallMethod.Normal =>
                        RunDllInjector(config.SteamPath, config),
                    GreenLumaInstallMethod.StealthAny =>
                        RunDllInjector(config.GreenLumaPath, config),
                    GreenLumaInstallMethod.User32 =>
                        RunSteamDirectly(config.SteamPath),
                    _ => false
                };

                if (result)
                {
                    Logger.Info($"Launch succeeded — method: {method}, waiting for Steam to start...");
                    var steamConfirmed = WaitForSteam(TimeSpan.FromSeconds(30));
                    if (steamConfirmed)
                    {
                        Logger.Info("Steam process confirmed running");
                    }
                    else
                    {
                        Logger.Error("Steam did not start within 30 seconds");
                        return false;
                    }
                }
                else
                {
                    Logger.Error($"Launch failed — method: {method}");
                }

                return result;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unhandled exception during launch");
                return false;
            }
        });
    }

    private static bool RunDllInjector(string injectorDir, Config config)
    {
        var injectorPath = Path.Combine(injectorDir, "DLLInjector.exe");
        if (!File.Exists(injectorPath))
        {
            Logger.Error($"DLLInjector.exe not found at '{injectorPath}'");
            return false;
        }

        Logger.Info($"Updating DLLInjector.ini in '{injectorDir}'");
        GreenLumaService.UpdateInjectorIni(config, injectorDir);

        Logger.Info($"Launching DLLInjector.exe from '{injectorDir}'");
        Process.Start(new ProcessStartInfo
        {
            FileName = injectorPath,
            WorkingDirectory = injectorDir,
            UseShellExecute = true
        });

        return true;
    }

    private static bool RunSteamDirectly(string? steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            Logger.Error("SteamPath is null or empty, cannot launch Steam directly");
            return false;
        }

        var steamExe = Path.Combine(steamPath, "Steam.exe");
        if (!File.Exists(steamExe))
        {
            Logger.Error($"Steam.exe not found at '{steamExe}'");
            return false;
        }

        Logger.Info($"Launching Steam.exe directly from '{steamPath}'");
        Process.Start(new ProcessStartInfo
        {
            FileName = steamExe,
            WorkingDirectory = steamPath,
            UseShellExecute = true
        });

        return true;
    }

    /// <summary>
    /// Waits for steam.exe to appear in the process list, which coincides with
    /// Steam's update/bootstrap window being displayed.
    /// Returns false if the process doesn't appear within the timeout.
    /// </summary>
    private static bool WaitForSteam(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            if (Process.GetProcessesByName("steam").Length > 0)
            {
                Logger.Debug($"steam.exe appeared after {stopwatch.Elapsed.TotalSeconds:F1}s");
                return true;
            }
            Thread.Sleep(500);
        }

        return false;
    }
}
