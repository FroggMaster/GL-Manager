namespace GreenLuma_Manager.Models;

public class GreenLumaVersionInfo
{
    /// <summary>
    /// Constant URL to the GreenLuma forum thread.
    /// </summary>
    public const string ForumUrl = "https://cs.rin.ru/forum/viewtopic.php?f=29&t=103709";

    /// <summary>
    /// Raw version string from the forum (e.g., "1.7.8").
    /// </summary>
    public required string LatestVersionTag { get; set; }

    /// <summary>
    /// Parsed semantic version from the latest version tag (e.g., 1.7.8).
    /// </summary>
    public Version? LatestSemanticVersion { get; set; }

    /// <summary>
    /// Version of the installed GreenLuma DLL, if detectable.
    /// </summary>
    public Version? InstalledVersion { get; set; }

    /// <summary>
    /// When the version check was last performed.
    /// </summary>
    public DateTime? CheckedAt { get; set; }

    /// <summary>
    /// Whether the last version check succeeded.
    /// </summary>
    public bool CheckSucceeded { get; set; }

    /// <summary>
    /// Error message if the last check failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Whether a newer version is available.
    /// Compares semantic versions directly.
    /// Returns false if comparison data is incomplete.
    /// </summary>
    public bool UpdateAvailable
    {
        get
        {
            // Can't determine if we lack either latest or installed data
            if (LatestSemanticVersion is null || InstalledVersion is null)
                return false;

            return LatestSemanticVersion > InstalledVersion;
        }
    }
}
