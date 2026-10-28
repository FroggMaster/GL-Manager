namespace GreenLuma_Manager.Models;

/// <summary>
/// Describes the result of importing a settings backup.
/// </summary>
public sealed class SettingsImportResult
{
    public SettingsImportResult(
        string previousStateBackupPath,
        int profileCount,
        bool includesPluginSettings)
    {
        PreviousStateBackupPath = previousStateBackupPath;
        ProfileCount = profileCount;
        IncludesPluginSettings = includesPluginSettings;
    }

    /// <summary>
    /// Backup file created from the state that was active before the import.
    /// </summary>
    public string PreviousStateBackupPath { get; }

    public int ProfileCount { get; }

    /// <summary>
    /// True when the imported archive contained plugin configuration.
    /// </summary>
    public bool IncludesPluginSettings { get; }
}
