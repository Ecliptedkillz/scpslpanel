using System.Collections.Generic;
using System.Runtime.Serialization;

namespace ScpSlPanel.LabApiBridge;

[DataContract]
internal sealed class HeartbeatPayload
{
    [DataMember(Name = "bridgeVersion")]
    public string BridgeVersion { get; set; } = "";

    [DataMember(Name = "apiVersion")]
    public string ApiVersion { get; set; } = "";

    [DataMember(Name = "roundState")]
    public string RoundState { get; set; } = "unknown";

    [DataMember(Name = "maxPlayers")]
    public int MaxPlayers { get; set; }

    [DataMember(Name = "players")]
    public List<PlayerPayload> Players { get; set; } = new();
}

[DataContract]
internal sealed class PlayerPayload
{
    [DataMember(Name = "playerId")]
    public int PlayerId { get; set; }

    [DataMember(Name = "displayName")]
    public string DisplayName { get; set; } = "";

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = "";

    [DataMember(Name = "ipAddress")]
    public string IpAddress { get; set; } = "";

    [DataMember(Name = "role")]
    public string Role { get; set; } = "";
}
