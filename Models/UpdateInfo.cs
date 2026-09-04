namespace GreenLuma_Manager.Models;

public class UpdateInfo
{
    public required string CurrentVersion { get; set; }
    public required string LatestVersion { get; set; }
    public required string LatestVersionTag { get; set; }
    public bool UpdateAvailable { get; set; }
    public required string DownloadUrl { get; set; }
    public required string ReleaseNotes { get; set; }
}