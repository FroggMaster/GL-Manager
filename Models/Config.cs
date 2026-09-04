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

    [DataMember(Name = "check_greenluma_updates")] public bool CheckGreenLumaUpdates { get; set; } = true;

    [DataMember] public string GreenLumaUsername { get; set; } = string.Empty;

    [DataMember] public string GreenLumaPassword { get; set; } = string.Empty;

    [DataMember] public bool ReplaceSteamAutostart { get; set; }

    [DataMember] public bool PrefetchAppList { get; set; }

    [DataMember] public bool FirstRun { get; set; } = true;

    /// <summary>
    /// When multiple install methods are available, the user's last choice is
    /// persisted here so it survives restarts.  Value is one of
    /// "User32", "Normal", "StealthAny", or null/empty (auto-detect).
    /// </summary>
    public string? PreferredMode { get; set; }

    /// <summary>
    /// Remembers the last valid StealthAny path the user configured.
    /// Used by Auto-Detect as an additional search location — invisible
    /// to the user, no UI.  Prevents losing the StealthAny path when
    /// Normal mode is auto-detected and overwrites GreenLumaPath.
    /// </summary>
    [DataMember] public string LastStealthAnyPath { get; set; } = string.Empty;
}