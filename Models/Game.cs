using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace GreenLuma_Manager.Models;

[DataContract]
public class Game : INotifyPropertyChanged
{
    [DataMember] public required string AppId { get; set; }

    [DataMember]
    public required string Name
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(AddAllDlcMenuHeader));
            }
        }
    } = string.Empty;

    [DataMember]
    public required string Type
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged(nameof(Type));
            }
        }
    }

    [DataMember]
    public string IconUrl
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged(nameof(IconUrl));
            }
        }
    } = string.Empty;

    [DataMember] public List<string> Depots { get; set; } = [];

    /// <summary>
    /// AppId of the parent game when this entry is a DLC, so the profile list can nest DLCs under
    /// their base game. Persisted; null for base games.
    /// </summary>
    [DataMember] public string? ParentAppId { get; set; }

    /// <summary>Low-res fallback URL from the store search API, used when icon caching fails.</summary>
    [IgnoreDataMember]
    public string FallbackIconUrl { get; set; } = string.Empty;

    [IgnoreDataMember]
    public bool IsEditing
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged(nameof(IsEditing));
            }
        }
    }

    [IgnoreDataMember]
    [JsonIgnore]
    public bool IsHighlighted
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged(nameof(IsHighlighted));
            }
        }
    }

    /// <summary>
    /// DLCs discovered for this game, nested under it in the search results. Transient state:
    /// marked with <see cref="JsonIgnoreAttribute"/> because the profile serializer
    /// (System.Text.Json) does not honor <see cref="IgnoreDataMemberAttribute"/>.
    /// </summary>
    [JsonIgnore]
    public ObservableCollection<Game> Dlcs { get; set; } = [];

    /// <summary>True when this game has nested DLCs to show. Transient.</summary>
    [JsonIgnore]
    public bool HasDlcs => Dlcs.Count > 0;

    /// <summary>Expansion state of the nested DLC list in the search results. Transient.</summary>
    [JsonIgnore]
    public bool IsExpanded
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged(nameof(IsExpanded));
            }
        }
    }

    /// <summary>
    /// Parent game when this entry is a DLC nested under a game in the search results. Transient.
    /// Used to show/hide the DLC row based on the parent's expansion state.
    /// </summary>
    [JsonIgnore]
    public Game? Parent { get; set; }

    /// <summary>True when this entry is a DLC nested under a parent game. Transient.</summary>
    [JsonIgnore]
    public bool IsDlc => Parent != null || !string.IsNullOrEmpty(ParentAppId);

    /// <summary>
    /// True when "Add All DLC to Profile" is meaningful for this entry (a base game, not a DLC).
    /// Transient.
    /// </summary>
    [JsonIgnore]
    public bool CanAddAllDlcs =>
        Parent == null &&
        string.IsNullOrEmpty(ParentAppId) &&
        string.Equals(Type, "Game", StringComparison.OrdinalIgnoreCase);

    /// <summary>Context-menu header text including the game name. Transient.</summary>
    [JsonIgnore]
    public string AddAllDlcMenuHeader => $"Add All DLC for {Name} to Profile";

    /// <summary>True when this game is currently in the active profile. Transient.</summary>
    [JsonIgnore]
    public bool IsInProfile
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged(nameof(IsInProfile));
            }
        }
    }

    /// <summary>
    /// True when this entry is a placeholder base game shown only to group orphan DLCs whose base
    /// game is not in the profile. It is not a real profile entry. Transient.
    /// </summary>
    [JsonIgnore]
    public bool IsVirtual { get; set; }

    /// <summary>
    /// Total number of DLCs Steam reports for this game. Used for the profile "installed/total"
    /// indicator. Transient.
    /// </summary>
    [JsonIgnore]
    public int TotalDlcCount
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged(nameof(TotalDlcCount));
                OnPropertyChanged(nameof(ProfileDlcLabel));
                OnPropertyChanged(nameof(HasProfileDlcLabel));
            }
        }
    }

    /// <summary>Search-results DLC indicator, for example "24 DLC". Transient.</summary>
    [JsonIgnore]
    public string SearchDlcLabel => Dlcs.Count > 0 ? $"{Dlcs.Count} DLC" : string.Empty;

    /// <summary>True when the search-results DLC indicator should be shown. Transient.</summary>
    [JsonIgnore]
    public bool HasSearchDlcLabel => Dlcs.Count > 0;

    /// <summary>
    /// Profile DLC indicator, for example "35/35 DLC" (installed / total). The total is never less
    /// than the installed count, because the Steam store undercounts delisted DLCs. Transient.
    /// </summary>
    [JsonIgnore]
    public string ProfileDlcLabel
    {
        get
        {
            var installed = Dlcs.Count;
            var total = Math.Max(TotalDlcCount, installed);
            return total > 0 ? $"{installed}/{total} DLC" : string.Empty;
        }
    }

    /// <summary>True when the profile DLC indicator should be shown. Transient.</summary>
    [JsonIgnore]
    public bool HasProfileDlcLabel => TotalDlcCount > 0;

    /// <summary>Notifies that the derived DLC indicators changed (after DLCs are linked).</summary>
    public void NotifyDlcIndicatorsChanged()
    {
        OnPropertyChanged(nameof(HasDlcs));
        OnPropertyChanged(nameof(SearchDlcLabel));
        OnPropertyChanged(nameof(HasSearchDlcLabel));
        OnPropertyChanged(nameof(ProfileDlcLabel));
        OnPropertyChanged(nameof(HasProfileDlcLabel));
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
