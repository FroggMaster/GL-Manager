namespace GreenLuma_Manager.Models;

/// <summary>
/// Specifies which proxy source to deploy during a fresh system DLL proxy
/// install. The SF variant only applies to the user32.dll proxy name.
/// </summary>
public enum User32DeployMode
{
    /// <summary>
    /// Copies the proxy matching the configured filename from the archive and
    /// AppListManager.exe to the Steam folder. For user32.dll this falls back
    /// to the SF variant when no plain user32.dll is available.
    /// </summary>
    User32,

    /// <summary>
    /// Copies user32SF.dll from the archive to the Steam folder, renames it to
    /// user32.dll, and also deploys AppListManager.exe. This is the
    /// recommended option for the user32.dll proxy name.
    /// </summary>
    User32SF
}
