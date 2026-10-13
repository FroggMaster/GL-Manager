using System.IO;
using System.Text;
using System.Text.Json;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Utilities;
using Newtonsoft.Json.Linq;

namespace GreenLuma_Manager.Services;

public class ProfileService
{
    private static readonly string ProfilesDir = PathDetector.ProfilesDir;

    public static List<Profile> LoadAll()
    {
        var profiles = new List<Profile>();
        try
        {
            PathDetector.EnsureExists(ProfilesDir);
            TryMigrateProfilesFromOldVersion();
            LoadProfilesFromDirectory(profiles);
            if (profiles.Count == 0) return CreateDefaultProfile(profiles);
            Logger.Info($"Loaded {profiles.Count} profile(s)");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load profiles");
        }

        return profiles;
    }


    private static List<Profile> CreateDefaultProfile(List<Profile> profiles)
    {
        var defaultProfile = new Profile { Name = "default" };
        Save(defaultProfile);
        profiles.Add(defaultProfile);
        return profiles;
    }

    private static void LoadProfilesFromDirectory(List<Profile> profiles)
    {
        foreach (var file in Directory.GetFiles(ProfilesDir, "*.json"))
        {
            Logger.Debug($"Loading profile from '{file}'");
            try
            {
                var profile = DeserializeProfile(File.ReadAllText(file, Encoding.UTF8));
                if (profile != null) profiles.Add(profile);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to load profile from '{file}'");
            }
        }
    }

    public static Profile? Load(string profileName)
    {
        try
        {
            var filePath = GetProfileFilePath(profileName);
            if (!File.Exists(filePath))
                return null;

            var json = File.ReadAllText(filePath, Encoding.UTF8);
            var profile = DeserializeProfile(json);
            Logger.Info($"Loaded profile '{profileName}'");
            return profile;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to load profile '{profileName}'");
            return null;
        }
    }

    public static void Save(Profile profile)
    {
        try
        {
            PathDetector.EnsureExists(ProfilesDir);
            var filePath = GetProfileFilePath(profile.Name);
            var json = SerializeProfile(profile);
            File.WriteAllText(filePath, json, Encoding.UTF8);
            Logger.Info($"Saved profile '{profile.Name}'");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to save profile '{profile.Name}'");
        }
    }

    public static void Delete(string profileName)
    {
        try
        {
            if (string.Equals(profileName, "default", StringComparison.OrdinalIgnoreCase))
                return;

            var filePath = GetProfileFilePath(profileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Logger.Info($"Deleted profile '{profileName}'");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to delete profile '{profileName}'");
        }
    }

    public static bool Rename(string oldName, string newName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
                return false;

            var oldPath = GetProfileFilePath(oldName);
            var newPath = GetProfileFilePath(newName);

            // Nothing to do when the name (and therefore the file) is unchanged.
            if (string.Equals(oldPath, newPath, StringComparison.Ordinal) &&
                string.Equals(oldName, newName, StringComparison.Ordinal))
                return true;

            if (!File.Exists(oldPath))
            {
                Logger.Warn($"Cannot rename profile: source '{oldPath}' does not exist");
                return false;
            }

            // Never overwrite an existing, differently named profile file.
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(newPath))
            {
                Logger.Warn($"Cannot rename profile: target '{newPath}' already exists");
                return false;
            }

            var profile = DeserializeProfile(File.ReadAllText(oldPath, Encoding.UTF8));
            if (profile == null)
            {
                Logger.Error($"Failed to rename profile: could not read '{oldPath}'");
                return false;
            }

            profile.Name = newName;
            File.WriteAllText(newPath, SerializeProfile(profile), Encoding.UTF8);

            // Only remove the old file when it is genuinely a different path.
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                File.Delete(oldPath);

            Logger.Info($"Renamed profile '{oldName}' to '{newName}'");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to rename profile '{oldName}' to '{newName}'");
            return false;
        }
    }

    public static void Export(Profile profile, string destinationPath)
    {
        try
        {
            var json = SerializeProfile(profile);
            File.WriteAllText(destinationPath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to export profile '{profile.Name}'");
        }
    }

    public static Profile? Import(string sourcePath)
    {
        try
        {
            var json = File.ReadAllText(sourcePath, Encoding.UTF8);
            return DeserializeProfile(json);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to import profile from '{sourcePath}'");
            return null;
        }
    }

    private static string GetProfileFilePath(string profileName)
    {
        var sanitizedName = SanitizeFileName(profileName);
        return Path.Combine(ProfilesDir, $"{sanitizedName}.json");
    }

    private static Profile? DeserializeProfile(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Profile>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string SerializeProfile(Profile profile)
    {
        return JsonSerializer.Serialize(profile);
    }

    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string([.. name.Select(c => invalidChars.Contains(c) ? '_' : c)]);
    }

    private static void TryMigrateProfilesFromOldVersion()
    {
        try
        {
            if (!Directory.Exists(ProfilesDir)) return;

            var filesToMigrate = Directory.GetFiles(ProfilesDir, "*.json");

            foreach (var file in filesToMigrate)
                try
                {
                    var rc3Json = File.ReadAllText(file, Encoding.UTF8);
                    var rc3Data = JObject.Parse(rc3Json);

                    if (rc3Data["games"] is not JArray gamesArray || gamesArray.Count == 0) continue;

                    if (gamesArray[0] is not JObject firstGame || firstGame["id"] == null) continue;

                    var profile = new Profile
                    {
                        Name = rc3Data["name"]?.ToString() ?? "default",
                        Games =
                        [
                            .. gamesArray
                                .Select(gameToken => new Game
                                {
                                    AppId = gameToken["id"]?.ToString() ?? string.Empty,
                                    Name = gameToken["name"]?.ToString() ?? string.Empty,
                                    Type = gameToken["type"]?.ToString() ?? "Game"
                                })
                                .Where(g => !string.IsNullOrEmpty(g.AppId))
                        ]
                    };

                    Save(profile);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Failed to migrate profile from '{file}'");
                }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to migrate profiles from old version");
        }
    }
}