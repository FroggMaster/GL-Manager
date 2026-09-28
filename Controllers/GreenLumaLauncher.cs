using System.Diagnostics;
using System.IO;
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
            var method = GreenLumaService.DetectInstallMethod(
                config.SteamPath, config.GreenLumaPath, config.PreferredMode);

            if (!LaunchDiagnostics.BeginRun(config, method, "GUI (MainWindow)"))
            {
                Logger.Warn("Launch refused: another launch is already in progress");
                return false;
            }

            try
            {
                Logger.Info($"Launch started — detected method: {method}");

                if (method == GreenLumaInstallMethod.None)
                {
                    Logger.Error("No GreenLuma installation detected, aborting launch");
                    LaunchDiagnostics.EndRun(false, "no GreenLuma installation detected");
                    return false;
                }

                if (!ValidatePaths(config))
                {
                    Logger.Error("Path validation failed, aborting launch");
                    LaunchDiagnostics.EndRun(false, "path validation failed");
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
                    Logger.Info($"Launch initiated — method: {method}, watching Steam lifecycle...");
                    LaunchDiagnostics.Step("Watching Steam lifecycle...");
                    result = LaunchDiagnostics.WatchSteam(
                        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(12), config.SteamPath, config.GreenLumaPath);

                    if (result)
                    {
                        Logger.Info("Steam process confirmed running at end of watch");
                        LaunchDiagnostics.Line("Steam confirmed running at end of watch");
                    }
                    else
                    {
                        Logger.Error("Steam did not appear, or was not running at the end of the watch");
                        LaunchDiagnostics.Line("Steam did not appear, or was not running at the end of the watch");
                    }
                }
                else
                {
                    Logger.Error($"Launch failed — method: {method}");
                }

                LaunchDiagnostics.EndRun(result, result
                    ? "Steam launched and stayed alive"
                    : "launch failed or Steam did not stay alive");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unhandled exception during launch");
                LaunchDiagnostics.EndRun(false, $"unhandled exception: {ex.GetType().Name}: {ex.Message}");
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
            LaunchDiagnostics.Line($"DLLInjector.exe not found at '{injectorPath}'");
            return false;
        }

        Logger.Info($"Updating DLLInjector.ini in '{injectorDir}'");
        LaunchDiagnostics.Step($"Updating DLLInjector.ini in '{injectorDir}'");
        GreenLumaService.UpdateInjectorIni(config, injectorDir);

        Logger.Info($"Launching DLLInjector.exe from '{injectorDir}'");
        LaunchDiagnostics.Step($"Launching DLLInjector.exe from '{injectorDir}'");
        return GreenLumaService.StartInjector(injectorPath, injectorDir);
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
}
