namespace GreenLuma_Manager.Models;

/// <summary>
/// Describes how GreenLuma is installed/detected on the system.
/// </summary>
public enum GreenLumaInstallMethod
{
    /// <summary>No GreenLuma installation detected.</summary>
    None,

    /// <summary>
    /// DLLInjector.exe + GreenLuma DLL at the root of the Steam folder.
    /// GreenLuma launches Steam via the injector.
    /// </summary>
    Normal,

    /// <summary>
    /// DLLInjector.exe + GreenLuma DLL in a subfolder of Steam or in a
    /// completely separate directory. Injects into a separately launched Steam.
    /// </summary>
    StealthAny,

    /// <summary>
    /// A modified system DLL proxy (user32.dll, version.dll, winmm.dll, or a
    /// custom name) sits in the Steam directory, hooking Steam's API calls
    /// directly. No injector needed, Steam starts normally. The member name is
    /// retained for backwards compatibility with saved preferences.
    /// </summary>
    User32
}
