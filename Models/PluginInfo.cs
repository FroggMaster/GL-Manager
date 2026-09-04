using System.Runtime.Serialization;

namespace GreenLuma_Manager.Models;

[DataContract]
public class PluginInfo
{
    [DataMember] public string Name { get; set; } = string.Empty;
    [DataMember] public string Version { get; set; } = string.Empty;
    [DataMember] public string Author { get; set; } = string.Empty;
    [DataMember] public string Description { get; set; } = string.Empty;
    [DataMember] public string FileName { get; set; } = string.Empty;
    [DataMember] public bool IsEnabled { get; set; } = true;
    [DataMember] public string Id { get; set; } = string.Empty;
}