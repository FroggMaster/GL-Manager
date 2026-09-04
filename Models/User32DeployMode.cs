namespace GreenLuma_Manager.Models;

/// <summary>
/// Specifies which user32 variant to deploy during a fresh User32-mode install.
/// </summary>
public enum User32DeployMode
{
    /// <summary>
    /// Copies user32.dll (directly found in the archive) and AppListManager.exe
    /// to the Steam folder. If no user32.dll is available in the archive, falls
    /// back to the SF variant.
    /// </summary>
    User32,

    /// <summary>
    /// Copies user32SF.dll from the archive to the Steam folder,
    /// renames it to user32.dll, and also deploys AppListManager.exe.
    /// This is the recommended option.
    /// </summary>
    User32SF
}
