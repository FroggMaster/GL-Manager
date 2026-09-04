using System.ComponentModel;
using System.Runtime.Serialization;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}