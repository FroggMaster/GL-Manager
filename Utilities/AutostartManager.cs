using System.IO;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;
using Microsoft.Win32;

namespace GreenLuma_Manager.Utilities;

public class AutostartManager
{
    private const string RunKeyPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string BackupKeyPath = "SOFTWARE\\GLM_Manager";
    private const string GreenLumaValueName = "GreenLumaManager";
    private const string GreenLumaMonitorValueName = "GreenLumaMonitor";

    public static void ManageAutostart(bool replaceSteam, Config? config)
    {
        try
        {
            Logger.Info($"Managing autostart: replaceSteam={replaceSteam}, greenLumaPath={config?.GreenLumaPath}");

            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);

            if (runKey == null)
                return;

            if (replaceSteam && !string.IsNullOrWhiteSpace(config?.GreenLumaPath))
                ReplaceWithGreenLuma(runKey, config);
            else
                RestoreOriginalSteam(runKey, config?.GreenLumaPath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to manage autostart");
        }
    }

    private static void ReplaceWithGreenLuma(RegistryKey runKey, Config? config)
    {
        if (config == null)
            return;

        Logger.Info("Replacing Steam autostart with GreenLuma Manager");

        var appPath = Environment.ProcessPath ??
                      Path.Combine(AppContext.BaseDirectory, AppDomain.CurrentDomain.FriendlyName);

        if (string.IsNullOrWhiteSpace(appPath))
            return;

        runKey.SetValue(GreenLumaMonitorValueName, $"\"{appPath}\" --launch-greenluma");
    }

    private static void RestoreOriginalSteam(RegistryKey runKey, string? greenlumaPath)
    {
        Logger.Info("Restoring original Steam autostart");
        runKey.DeleteValue(GreenLumaMonitorValueName, false);
        CleanupVbsScript(greenlumaPath);
    }

    private static void CleanupVbsScript(string? greenlumaPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(greenlumaPath))
            {
                var vbsPath = Path.Combine(greenlumaPath, "GLM_Autostart.vbs");
                if (File.Exists(vbsPath)) File.Delete(vbsPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to cleanup VBS script at: {greenlumaPath}");
        }
    }

    public static void CleanupAll()
    {
        try
        {
            Logger.Info("Cleaning up all autostart entries");
            RemoveGreenLumaAutostart();
            RemoveGreenLumaMonitor();
            DeleteBackupKey();
            CleanupAllVbsScripts();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to cleanup all autostart entries");
        }
    }

    private static void CleanupAllVbsScripts()
    {
        try
        {
            Logger.Info("Cleaning up VBS scripts from common Steam paths");

            string[] commonPaths =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Steam"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Steam"),
                "C:\\GreenLuma"
            ];

            foreach (var basePath in commonPaths)
                try
                {
                    var vbsPath = Path.Combine(basePath, "GLM_Autostart.vbs");
                    if (File.Exists(vbsPath)) File.Delete(vbsPath);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Failed to delete VBS script at: {basePath}");
                }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to cleanup VBS scripts");
        }
    }

    private static void RemoveGreenLumaAutostart()
    {
        try
        {
            Logger.Info("Removing GreenLuma autostart registry entry");
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            runKey?.DeleteValue(GreenLumaValueName, false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to remove GreenLuma autostart entry");
        }
    }

    private static void RemoveGreenLumaMonitor()
    {
        try
        {
            Logger.Info("Removing GreenLuma monitor registry entry");
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            runKey?.DeleteValue(GreenLumaMonitorValueName, false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to remove GreenLuma monitor entry");
        }
    }

    private static void DeleteBackupKey()
    {
        try
        {
            Logger.Info("Deleting backup registry key");
            Registry.CurrentUser.DeleteSubKeyTree(BackupKeyPath, false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to delete backup registry key");
        }
    }
}