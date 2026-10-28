using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Utilities;

namespace GreenLuma_Manager.Services;

/// <summary>
/// Exports and imports a portable backup of the persisted application state:
/// the config file, saved profiles, and plugin configuration. Plugin binaries,
/// icon caches, WebView2 data, and logs are not included.
/// </summary>
public static class SettingsBackupService
{
    private const string ManifestEntryName = "manifest.json";
    private const string ConfigEntryName = "config.json";
    private const string PluginsEntryName = "plugins.json";
    private const string ProfilesEntryPrefix = "profiles/";
    private const string ManifestFormat = "GreenLumaManagerBackup";
    private const int ManifestFormatVersion = 1;
    private const int MaxEntryCount = 10_000;
    private const long MaxEntryBytes = 20L * 1024 * 1024;
    private const long MaxTotalBytes = 100L * 1024 * 1024;

    private static readonly string ConfigPath =
        Path.Combine(PathDetector.ConfigDir, "config.json");

    private static readonly string PluginsConfigPath =
        Path.Combine(PathDetector.AppDataDir, "plugins.json");

    private static readonly string BackupsDir =
        Path.Combine(PathDetector.AppDataDir, "backups");

    public static void Export(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("A destination file is required.", nameof(destinationPath));

        var fullDestination = Path.GetFullPath(destinationPath);
        EnsureDestinationIsSafe(fullDestination);

        CreateArchive(fullDestination);
        Logger.Info($"Exported settings backup to '{fullDestination}'");
    }

    public static SettingsImportResult Import(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("A backup file is required.", nameof(sourcePath));

        var fullSource = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSource))
            throw new FileNotFoundException("The selected backup file was not found.", fullSource);

        var stagingDirectory = Path.Combine(Path.GetTempPath(), $"GLM_Import_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var staged = ExtractAndValidate(fullSource, stagingDirectory);

            // Always keep a rollback point before touching live files.
            Directory.CreateDirectory(BackupsDir);
            var previousBackupPath = Path.Combine(
                BackupsDir,
                $"pre-import-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip");

            CreateArchive(previousBackupPath);

            try
            {
                ApplyStagedState(staged);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to apply imported settings; restoring the previous state");

                var restored = TryRestoreFromBackup(previousBackupPath, stagingDirectory);

                throw new InvalidOperationException(
                    restored
                        ? $"The backup could not be applied: {ex.Message}\n\nYour previous settings were restored."
                        : $"The backup could not be applied: {ex.Message}\n\n" +
                          "Your previous state could not be restored automatically. " +
                          $"A backup was saved to:\n{previousBackupPath}",
                    ex);
            }

            ApplyAutostart(staged.ImportedConfig);

            Logger.Info(
                $"Imported settings backup from '{fullSource}' ({staged.ProfilePaths.Count} profile(s))");

            return new SettingsImportResult(
                previousBackupPath,
                staged.ProfilePaths.Count,
                staged.PluginsPath != null);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static void CreateArchive(string destinationPath)
    {
        if (!File.Exists(ConfigPath))
            throw new FileNotFoundException("The configuration file was not found.", ConfigPath);

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory) ||
            !Directory.Exists(destinationDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The backup folder does not exist: {destinationDirectory}");
        }

        var tempPath = Path.Combine(
            destinationDirectory,
            $".GLM_backup_{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteTextEntry(archive, ManifestEntryName, CreateManifestJson());
                WriteFileEntry(archive, ConfigEntryName, ConfigPath);

                if (File.Exists(PluginsConfigPath))
                    WriteFileEntry(archive, PluginsEntryName, PluginsConfigPath);

                AddProfileEntries(archive);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static string CreateManifestJson()
    {
        return JsonSerializer.Serialize(
            new
            {
                Format = ManifestFormat,
                FormatVersion = ManifestFormatVersion,
                AppVersion = MainWindow.Version,
                CreatedUtc = DateTime.UtcNow.ToString("O")
            },
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteFileEntry(ZipArchive archive, string entryName, string sourcePath)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

        sourceStream.CopyTo(entryStream);
    }

    private static void AddProfileEntries(ZipArchive archive)
    {
        var profilesDirectory = PathDetector.ProfilesDir;
        if (!Directory.Exists(profilesDirectory))
            return;

        foreach (var profilePath in Directory.EnumerateFiles(profilesDirectory, "*.json"))
        {
            var fileName = Path.GetFileName(profilePath);
            if (!IsSafeProfileFileName(fileName))
                continue;

            WriteFileEntry(archive, ProfilesEntryPrefix + fileName, profilePath);
        }
    }

    private static StagedBackup ExtractAndValidate(string archivePath, string stagingDirectory)
    {
        using var archive = OpenArchive(archivePath);

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var fileCount = 0;
        long declaredTotal = 0;

        foreach (var entry in archive.Entries)
        {
            if (IsDirectoryEntry(entry))
                continue;

            var name = NormalizeEntryName(entry.FullName);
            if (!IsAllowedEntryName(name))
                throw new InvalidDataException($"The backup contains an unexpected file: '{name}'.");

            fileCount++;
            if (fileCount > MaxEntryCount)
                throw new InvalidDataException("The backup contains too many files.");

            if (entry.Length > MaxEntryBytes)
                throw new InvalidDataException($"The backup entry '{name}' is too large.");

            declaredTotal += entry.Length;
            if (declaredTotal > MaxTotalBytes)
                throw new InvalidDataException("The backup is too large to import.");

            if (!entries.TryAdd(name, entry))
                throw new InvalidDataException($"The backup contains a duplicate entry: '{name}'.");
        }

        if (!entries.TryGetValue(ManifestEntryName, out var manifestEntry))
            throw new InvalidDataException("The selected file is not a GreenLuma Manager backup.");

        ValidateManifest(manifestEntry);

        if (!entries.TryGetValue(ConfigEntryName, out var configEntry))
            throw new InvalidDataException("The backup does not contain a configuration file.");

        var remainingBytes = MaxTotalBytes;

        var stagedConfigPath = ExtractEntry(
            configEntry,
            stagingDirectory,
            ConfigEntryName,
            ref remainingBytes);

        var importedConfig = ValidateConfigFile(stagedConfigPath);

        string? stagedPluginsPath = null;
        if (entries.TryGetValue(PluginsEntryName, out var pluginsEntry))
        {
            stagedPluginsPath = ExtractEntry(
                pluginsEntry,
                stagingDirectory,
                PluginsEntryName,
                ref remainingBytes);

            ValidatePluginsFile(stagedPluginsPath);
        }

        var profilePaths = new List<string>();

        foreach (var pair in entries)
        {
            if (!pair.Key.StartsWith(ProfilesEntryPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var fileName = pair.Key[ProfilesEntryPrefix.Length..];
            var relativePath = Path.Combine("profiles", fileName);

            var stagedPath = ExtractEntry(
                pair.Value,
                stagingDirectory,
                relativePath,
                ref remainingBytes);

            ValidateProfileFile(stagedPath, pair.Key);
            profilePaths.Add(stagedPath);
        }

        return new StagedBackup
        {
            ConfigPath = stagedConfigPath,
            PluginsPath = stagedPluginsPath,
            ProfilePaths = profilePaths,
            ImportedConfig = importedConfig
        };
    }

    private static ZipArchive OpenArchive(string archivePath)
    {
        try
        {
            return ZipFile.OpenRead(archivePath);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                "The selected file is not a valid GreenLuma Manager backup.",
                ex);
        }
    }

    private static void ValidateManifest(ZipArchiveEntry entry)
    {
        string json;
        using (var stream = entry.Open())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            json = reader.ReadToEnd();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The backup manifest is invalid.");

            var format = root.TryGetProperty("Format", out var formatElement) &&
                         formatElement.ValueKind == JsonValueKind.String
                ? formatElement.GetString()
                : null;

            var formatVersion = root.TryGetProperty("FormatVersion", out var versionElement) &&
                                versionElement.TryGetInt32(out var parsedVersion)
                ? parsedVersion
                : 0;

            if (!string.Equals(format, ManifestFormat, StringComparison.Ordinal) ||
                formatVersion != ManifestFormatVersion)
            {
                throw new InvalidDataException(
                    "The selected file is not a supported GreenLuma Manager backup.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The backup manifest is invalid.", ex);
        }
    }

    private static Config ValidateConfigFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The backup configuration is invalid.");

            var config = JsonSerializer.Deserialize<Config>(json);
            if (config == null)
                throw new InvalidDataException("The backup configuration is empty.");

            if (config.SteamPath == null ||
                config.GreenLumaPath == null ||
                config.LastProfile == null ||
                config.LastAppListProfile == null ||
                config.LastAppListFingerprint == null ||
                config.GreenLumaUsername == null ||
                config.GreenLumaPassword == null ||
                config.SteamApiKey == null ||
                config.SystemProxyDllName == null ||
                config.DeployedSystemProxyDll == null ||
                config.LastStealthAnyPath == null)
            {
                throw new InvalidDataException(
                    "The backup configuration is missing required values.");
            }

            return config;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The backup configuration is invalid.", ex);
        }
    }

    private static void ValidatePluginsFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The backup plugin configuration is invalid.");

            var plugins = JsonSerializer.Deserialize<List<PluginInfo?>>(json);
            if (plugins == null)
                throw new InvalidDataException("The backup plugin configuration is empty.");

            foreach (var plugin in plugins)
            {
                if (plugin == null ||
                    string.IsNullOrWhiteSpace(plugin.FileName) ||
                    !string.Equals(
                        plugin.FileName,
                        Path.GetFileName(plugin.FileName),
                        StringComparison.Ordinal) ||
                    plugin.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    throw new InvalidDataException(
                        "The backup contains an invalid plugin file name.");
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The backup plugin configuration is invalid.", ex);
        }
    }

    private static void ValidateProfileFile(string path, string entryName)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"The backup profile '{entryName}' is invalid.");

            var profile = JsonSerializer.Deserialize<Profile>(json);
            if (profile == null ||
                string.IsNullOrWhiteSpace(profile.Name) ||
                profile.Games == null)
            {
                throw new InvalidDataException($"The backup profile '{entryName}' is invalid.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The backup profile '{entryName}' is invalid.", ex);
        }
    }

    private static void ApplyStagedState(StagedBackup staged)
    {
        Directory.CreateDirectory(PathDetector.ConfigDir);

        var configTarget = Path.Combine(PathDetector.ConfigDir, "config.json");
        ReplaceFile(staged.ConfigPath, configTarget);

        var pluginsTarget = Path.Combine(PathDetector.AppDataDir, "plugins.json");
        if (staged.PluginsPath != null)
        {
            ReplaceFile(staged.PluginsPath, pluginsTarget);
        }
        else if (File.Exists(pluginsTarget))
        {
            // A complete restore makes the imported state authoritative.
            File.Delete(pluginsTarget);
        }

        // Pending deletions belong to the previous state. Keeping them could
        // delete a plugin file that the imported configuration now references.
        var pendingDeletesPath = Path.Combine(PathDetector.AppDataDir, "pending_deletes.json");
        if (File.Exists(pendingDeletesPath))
            File.Delete(pendingDeletesPath);

        ApplyProfiles(staged.ProfilePaths);
    }

    private static void ReplaceFile(string sourcePath, string targetPath)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new InvalidOperationException($"Invalid target path: {targetPath}");

        Directory.CreateDirectory(targetDirectory);

        var tempPath = Path.Combine(targetDirectory, $".import_{Guid.NewGuid():N}.tmp");

        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void ApplyProfiles(IReadOnlyList<string> stagedProfilePaths)
    {
        var profilesDirectory = PathDetector.ProfilesDir;
        var parentDirectory = Path.GetDirectoryName(profilesDirectory) ?? PathDetector.AppDataDir;

        Directory.CreateDirectory(parentDirectory);

        var newProfilesDirectory = Path.Combine(
            parentDirectory,
            $".profiles-new-{Guid.NewGuid():N}");

        Directory.CreateDirectory(newProfilesDirectory);

        try
        {
            foreach (var stagedPath in stagedProfilePaths)
            {
                var fileName = Path.GetFileName(stagedPath);
                var destination = Path.Combine(newProfilesDirectory, fileName);
                File.Copy(stagedPath, destination, overwrite: false);
            }

            var oldProfilesDirectory = Path.Combine(
                parentDirectory,
                $".profiles-old-{Guid.NewGuid():N}");

            var hadExistingProfiles = Directory.Exists(profilesDirectory);

            if (hadExistingProfiles)
                Directory.Move(profilesDirectory, oldProfilesDirectory);

            try
            {
                Directory.Move(newProfilesDirectory, profilesDirectory);
            }
            catch
            {
                if (hadExistingProfiles && !Directory.Exists(profilesDirectory))
                    Directory.Move(oldProfilesDirectory, profilesDirectory);

                throw;
            }

            if (hadExistingProfiles)
                TryDeleteDirectory(oldProfilesDirectory);
        }
        finally
        {
            TryDeleteDirectory(newProfilesDirectory);
        }
    }

    private static void ApplyAutostart(Config? config)
    {
        if (config == null)
            return;

        try
        {
            AutostartManager.ManageAutostart(config.ReplaceSteamAutostart, config);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to apply autostart settings from the imported backup");
        }
    }

    private static bool TryRestoreFromBackup(string backupPath, string stagingRoot)
    {
        var restoreDirectory = Path.Combine(stagingRoot, "restore");

        try
        {
            Directory.CreateDirectory(restoreDirectory);
            var staged = ExtractAndValidate(backupPath, restoreDirectory);
            ApplyStagedState(staged);
            Logger.Info("Restored the previous settings after a failed import");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to restore the previous settings after a failed import");
            return false;
        }
    }

    private static string ExtractEntry(
        ZipArchiveEntry entry,
        string stagingDirectory,
        string relativePath,
        ref long remainingBytes)
    {
        var stagingRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(stagingDirectory));

        var destinationPath = Path.GetFullPath(Path.Combine(stagingRoot, relativePath));

        if (!destinationPath.StartsWith(
                stagingRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The backup contains an invalid path: '{entry.FullName}'.");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        using var source = entry.Open();
        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);

        var buffer = new byte[81920];
        int read;

        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            remainingBytes -= read;
            if (remainingBytes < 0)
                throw new InvalidDataException("The backup expands to more data than expected.");

            destination.Write(buffer, 0, read);
        }

        return destinationPath;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
    {
        return entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
               entry.FullName.EndsWith("\\", StringComparison.Ordinal) ||
               string.IsNullOrEmpty(entry.Name);
    }

    private static string NormalizeEntryName(string name) => name.Replace('\\', '/');

    private static bool IsAllowedEntryName(string name)
    {
        if (name.Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(ConfigEntryName, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(PluginsEntryName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.StartsWith(ProfilesEntryPrefix, StringComparison.OrdinalIgnoreCase) &&
               IsSafeProfileFileName(name[ProfilesEntryPrefix.Length..]);
    }

    private static bool IsSafeProfileFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (fileName is "." or "..")
            return false;

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        return string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal);
    }

    private static void EnsureDestinationIsSafe(string destinationPath)
    {
        if (PathsEqual(destinationPath, ConfigPath) ||
            PathsEqual(destinationPath, PluginsConfigPath))
        {
            throw new InvalidOperationException(
                "Choose a different file name. This path is used by the application's live settings.");
        }

        var profilesDirectory = Path.GetFullPath(PathDetector.ProfilesDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (destinationPath.StartsWith(profilesDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Choose a backup location outside the profiles folder.");
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not delete temporary directory '{path}': {ex.Message}");
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not delete temporary file '{path}': {ex.Message}");
        }
    }

    private sealed class StagedBackup
    {
        public string ConfigPath { get; init; } = string.Empty;
        public string? PluginsPath { get; init; }
        public List<string> ProfilePaths { get; init; } = [];
        public Config? ImportedConfig { get; init; }
    }
}
