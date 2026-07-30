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
internal sealed class BanCheckPayload
{
    [DataMember(Name = "banned")] public bool Banned { get; set; }
    [DataMember(Name = "reason")] public string? Reason { get; set; }
    [DataMember(Name = "expiresAt")] public string? ExpiresAt { get; set; }
}

[DataContract]
internal sealed class GameRolePayload
{
    [DataMember(Name = "assigned")] public bool Assigned { get; set; }
    [DataMember(Name = "groupName")] public string? GroupName { get; set; }
    [DataMember(Name = "discordId")] public string? DiscordId { get; set; }
    [DataMember(Name = "discordRoleId")] public string? DiscordRoleId { get; set; }
    [DataMember(Name = "permissions")] public List<string> Permissions { get; set; } = new();
    [DataMember(Name = "badgeText")] public string BadgeText { get; set; } = "";
    [DataMember(Name = "badgeColor")] public string BadgeColor { get; set; } = "silver";
    [DataMember(Name = "hidden")] public bool Hidden { get; set; }
    [DataMember(Name = "cover")] public bool Cover { get; set; }
    [DataMember(Name = "reservedSlot")] public bool ReservedSlot { get; set; }
    [DataMember(Name = "kickPower")] public byte KickPower { get; set; }
    [DataMember(Name = "requiredKickPower")] public byte RequiredKickPower { get; set; }
    [DataMember(Name = "pluginPermissions")] public List<string> PluginPermissions { get; set; } = new();
}

[DataContract]
internal sealed class CustomBadgePayload
{
    [DataMember(Name = "assigned")] public bool Assigned { get; set; }
    [DataMember(Name = "badgeText")] public string BadgeText { get; set; } = "";
    [DataMember(Name = "badgeColor")] public string BadgeColor { get; set; } = "silver";
}

[DataContract]
internal sealed class TagOptionsPayload
{
    [DataMember(Name = "options")] public List<TagOptionPayload> Options { get; set; } = new();
    [DataMember(Name = "selectedId")] public string? SelectedId { get; set; }
}

[DataContract]
internal sealed class TagOptionPayload
{
    [DataMember(Name = "id")] public string Id { get; set; } = "";
    [DataMember(Name = "type")] public string Type { get; set; } = "";
    [DataMember(Name = "badgeText")] public string BadgeText { get; set; } = "";
    [DataMember(Name = "badgeColor")] public string BadgeColor { get; set; } = "silver";
}

[DataContract]
internal sealed class TagPreferencePayload
{
    [DataMember(Name = "selectedId")] public string? SelectedId { get; set; }
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
    [DataMember(Name = "targetUserId")] public string? TargetUserId { get; set; }
    [DataMember(Name = "targetDisplayName")] public string? TargetDisplayName { get; set; }
}
