using System;
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

    [DataMember(Name = "ping")]
    public int Ping { get; set; }

    [DataMember(Name = "sessionSeconds")]
    public long SessionSeconds { get; set; }

    [DataMember(Name = "isMuted")]
    public bool IsMuted { get; set; }
}

[DataContract]
internal sealed class CommandPayload
{
    [DataMember(Name = "id")] public Guid Id { get; set; }
    [DataMember(Name = "type")] public string Type { get; set; } = "";
    [DataMember(Name = "playerId")] public string? PlayerId { get; set; }
    [DataMember(Name = "reason")] public string? Reason { get; set; }
    [DataMember(Name = "durationSeconds")] public int? DurationSeconds { get; set; }
    [DataMember(Name = "message")] public string? Message { get; set; }
}

[DataContract]
internal sealed class CommandResultPayload
{
    [DataMember(Name = "success")] public bool Success { get; set; }
    [DataMember(Name = "message")] public string? Message { get; set; }
}

[DataContract]
internal sealed class EventPayload
{
    [DataMember(Name = "type")] public string Type { get; set; } = "";
    [DataMember(Name = "at")] public string At { get; set; } = "";
    [DataMember(Name = "playerId")] public string? PlayerId { get; set; }
    [DataMember(Name = "userId")] public string? UserId { get; set; }
    [DataMember(Name = "displayName")] public string? DisplayName { get; set; }
    [DataMember(Name = "detail")] public string? Detail { get; set; }
    [DataMember(Name = "durationSeconds")] public int? DurationSeconds { get; set; }
    [DataMember(Name = "actor")] public string? Actor { get; set; }
}
