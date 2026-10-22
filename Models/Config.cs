using System.Runtime.Serialization;

namespace GreenLuma_Manager.Models;

[DataContract]
public class Config
{
    [DataMember] public string SteamPath { get; set; } = string.Empty;

    [DataMember] public string GreenLumaPath { get; set; } = string.Empty;

    [DataMember] public bool NoHook { get; set; }

    [DataMember] public bool DisableUpdateCheck { get; set; }

    [DataMember] public bool AutoUpdate { get; set; } = true;

    [DataMember] public string LastProfile { get; set; } = "default";

    /// <summary>
    /// Name of the profile the on-disk AppList.ini was generated for.
    /// Empty means unknown (existing users), which is treated as stale.
    /// </summary>
    [DataMember] public string LastAppListProfile { get; set; } = string.Empty;

    /// <summary>
    /// Fingerprint of the profile state that produced the on-disk AppList.ini.
    /// Empty means unknown (existing users), which is treated as stale.
    /// </summary>
    [DataMember] public string LastAppListFingerprint { get; set; } = string.Empty;

    [DataMember(Name = "check_greenluma_updates")] public bool CheckGreenLumaUpdates { get; set; } = true;

    [DataMember] public string GreenLumaUsername { get; set; } = string.Empty;

    [DataMember] public string GreenLumaPassword { get; set; } = string.Empty;

    [DataMember] public bool ReplaceSteamAutostart { get; set; }

    [DataMember] public bool PrefetchAppList { get; set; }

    /// <summary>
    /// Optional user-provided Steam Web API key (steamcommunity.com/dev/apikey) used to fetch the
    /// full Steam app list for local search. Empty disables the app-list feature.
    /// </summary>
    [DataMember] public string SteamApiKey { get; set; } = string.Empty;

    [DataMember] public bool FirstRun { get; set; } = true;

    /// <summary>
    /// When multiple install methods are available, the user's last choice is
    /// persisted here so it survives restarts.  Value is one of
    /// "User32", "Normal", "StealthAny", or null/empty (auto-detect).
    /// The "User32" value is retained for backwards compatibility and now
    /// represents the System DLL Proxy install method.
    /// </summary>
    public string? PreferredMode { get; set; }

    /// <summary>
    /// Filename of the system DLL proxy GreenLumaRE loads from the Steam
    /// folder. Defaults to user32.dll so existing installations and old
    /// config files keep their current behavior.
    /// </summary>
    [DataMember] public string SystemProxyDllName { get; set; } = "user32.dll";

    /// <summary>
    /// Filename of the proxy DLL this application deployed or adopted.
    /// Empty means unknown (legacy installation). Used to avoid disabling or
    /// overwriting a proxy that belongs to another tool.
    /// </summary>
    [DataMember] public string DeployedSystemProxyDll { get; set; } = string.Empty;

    /// <summary>
    /// Remembers the last valid StealthAny path the user configured.
    /// Used by Auto-Detect as an additional search location — invisible
    /// to the user, no UI.  Prevents losing the StealthAny path when
    /// Normal mode is auto-detected and overwrites GreenLumaPath.
    /// </summary>
    [DataMember] public string LastStealthAnyPath { get; set; } = string.Empty;
}