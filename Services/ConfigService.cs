using System.IO;
using System.Text;
using System.Text.Json;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Utilities;
using Newtonsoft.Json.Linq;

namespace GreenLuma_Manager.Services;

public class ConfigService
{
    private static readonly string ConfigDir = PathDetector.ConfigDir;

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");


    public static Config Load()
    {
        try
        {
            PathDetector.EnsureExists(ConfigDir);

            if (!File.Exists(ConfigPath)) return CreateDefaultConfig();

            var configJson = File.ReadAllText(ConfigPath, Encoding.UTF8);

            var migratedConfig = TryMigrateFromOldVersion(configJson);
            if (migratedConfig != null)
            {
                Logger.Info("Config loaded (migrated)");
                return migratedConfig;
            }

            var config = DeserializeConfig(configJson) ?? new Config();
            Logger.Info("Config loaded");
            return config;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load config");
            return new Config();
        }
    }


    private static Config CreateDefaultConfig()
    {
        var config = new Config();
        var (steamPath, greenLumaPath) = PathDetector.DetectPaths();

        config.SteamPath = steamPath;
        config.GreenLumaPath = greenLumaPath;

        Save(config);
        Logger.Info("Created default config");
        return config;
    }

    private static Config? DeserializeConfig(string json)
    {
        try
        {
            Logger.Debug("Deserializing config JSON");
            return JsonSerializer.Deserialize<Config>(json);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to deserialize config");
            return null;
        }
    }

    private static Config? TryMigrateFromOldVersion(string configJson)
    {
        try
        {
            var jsonData = JObject.Parse(configJson);

            if (jsonData["steam_path"] == null && jsonData["SteamPath"] != null) return null;

            if (jsonData["steam_path"] != null)
            {
                var config = new Config
                {
                    SteamPath = jsonData["steam_path"]?.ToString() ?? string.Empty,
                    GreenLumaPath = jsonData["greenluma_path"]?.ToString() ?? string.Empty,
                    NoHook = jsonData["no_hook"]?.ToObject<bool>() ?? false,
                    DisableUpdateCheck = jsonData["disable_update_check"]?.ToObject<bool>() ?? false,
                    AutoUpdate = jsonData["auto_update"]?.ToObject<bool>() ?? true,
                    LastProfile = jsonData["last_profile"]?.ToString() ?? "default",
                    CheckGreenLumaUpdates = jsonData["check_greenluma_updates"]?.ToObject<bool>()
                        ?? jsonData["check_update"]?.ToObject<bool>()
                        ?? true,
                    ReplaceSteamAutostart = jsonData["replace_steam_autostart"]?.ToObject<bool>() ?? false,
                    PrefetchAppList = jsonData["prefetch_app_list"]?.ToObject<bool>() ?? false,
                    FirstRun = false
                };

                SerializeConfig(config);
                Logger.Warn("Config migrated from old version (snake_case)");
                return config;
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to migrate config from old version");
            return null;
        }
    }

    public static void Save(Config config)
    {
        try
        {
            PathDetector.EnsureExists(ConfigDir);

            var json = SerializeConfig(config);
            File.WriteAllText(ConfigPath, json, Encoding.UTF8);
            Logger.Info("Config saved");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save config");
        }
    }

    private static string SerializeConfig(Config config)
    {
        return JsonSerializer.Serialize(config);
    }

    public static void WipeData()
    {
        try
        {
            AutostartManager.CleanupAll();

            if (Directory.Exists(ConfigDir)) Directory.Delete(ConfigDir, true);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to wipe config data");
        }
    }
}