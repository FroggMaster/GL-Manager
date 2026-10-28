namespace GreenLuma_Manager.Models;

/// <summary>
/// Describes the GreenLuma DLL installed in the GreenLuma directory.
/// </summary>
public sealed class GreenLumaInstallationInfo
{
    /// <summary>
    /// Product name embedded in the DLL, for example "GreenLuma" or "GreenLumaRE".
    /// </summary>
    public required string ProductName { get; init; }

    /// <summary>
    /// True when the installed DLL is the GreenLumaRE variant.
    /// </summary>
    public required bool IsGreenLumaRe { get; init; }

    /// <summary>
    /// Installed version read from the DLL metadata, if available.
    /// </summary>
    public Version? Version { get; init; }

    /// <summary>
    /// Absolute path of the installed GreenLuma DLL.
    /// </summary>
    public string? DllPath { get; init; }
}
