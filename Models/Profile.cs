using System.Runtime.Serialization;

namespace GreenLuma_Manager.Models;

[DataContract]
public class Profile
{
    [DataMember] public string Name { get; set; } = "New Profile";

    [DataMember] public List<Game> Games { get; set; } = [];
}