using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScpSlPanel.Api.Domain;

public sealed class SnowflakeStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? "",
            JsonTokenType.Number => reader.GetUInt64().ToString(),
            JsonTokenType.Null => "",
            _ => throw new JsonException("Discord snowflake must be a string or number.")
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

public enum ServerState { Offline, Starting, Online, Stopping, Faulted }

public sealed record ServerDefinition(
    Guid Id, string Name, string ExecutablePath, string Arguments, string WorkingDirectory,
    bool AutoRestart, bool AutoStart, int QueryPort, string? UpdateCommand, DateTimeOffset CreatedAt,
    string? BridgeToken = null, string Icon = "gamepad", string AccentColor = "#e44343");

public sealed record ServerSnapshot(
    Guid Id, string Name, ServerState State, int? ProcessId, DateTimeOffset? StartedAt,
    long MemoryBytes, double CpuPercent, int Players, int MaxPlayers, string? LastError,
    string Icon = "gamepad", string AccentColor = "#e44343");
public sealed record ManagedProcessRecord(
    Guid ServerId, int ProcessId, string ExecutablePath, DateTimeOffset StartedAt);

public sealed record PlayerInfo(
    string Id, string Nickname, string UserId, string IpAddress, string Role, int Ping,
    DateTimeOffset ConnectedAt, long SessionSeconds = 0, bool IsMuted = false);

public sealed record BanEntry(
    Guid Id, string Target, string DisplayName, string Reason, string IssuedBy,
    DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt, bool Revoked,
    Guid? ServerId = null, string? UserId = null, string? IpAddress = null);

public sealed record BridgeBanCheck(
    bool Banned, string? Reason = null, DateTimeOffset? ExpiresAt = null);

public sealed record AuditEntry(
    Guid Id, DateTimeOffset At, string Actor, string Action, string Target, string Detail);

public sealed record ScheduleEntry(
    Guid Id, Guid ServerId, string Name, string Cron, string Action, bool Enabled,
    DateTimeOffset? LastRunAt, int WarningSeconds = 0);

public sealed record PanelUser(
    Guid Id, string Username, string PasswordHash, string Role, bool Enabled,
    DateTimeOffset CreatedAt, IReadOnlyList<Guid>? ServerIds = null,
    IReadOnlyList<string>? Permissions = null, IReadOnlyList<ServerAccessGrant>? ServerAccess = null,
    string? TotpSecret = null, bool TotpEnabled = false, int SessionVersion = 0);
public sealed record PanelSession(
    Guid Id, Guid UserId, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt,
    string IpAddress, string UserAgent, bool Revoked = false);
public sealed record ServerAccessGrant(Guid ServerId, IReadOnlyList<string> Permissions);

public sealed record PluginEntry(
    string Name, string Version, string Framework, bool Enabled, string Path);

public sealed record DashboardOverview(
    int ServersOnline, int ServersTotal, int PlayersOnline, long MemoryBytes,
    IReadOnlyList<ServerSnapshot> Servers, IReadOnlyList<AuditEntry> RecentActivity);

public sealed record ServerCreateRequest(
    string Name, string ExecutablePath, string? Arguments, string? WorkingDirectory,
    bool AutoRestart, bool AutoStart, int QueryPort, string? UpdateCommand,
    string Icon = "gamepad", string AccentColor = "#e44343");

public sealed record LoginRequest(string Username, string Password, string? Code = null);
public sealed record TotpRequest(string Code);
public sealed record AccountRequest(
    string Username, string? Password, bool Enabled, IReadOnlyList<Guid> ServerIds,
    IReadOnlyList<string> Permissions, IReadOnlyList<ServerAccessGrant>? ServerAccess = null);
public sealed record CommandRequest(string Command);
public sealed record ModerationRequest(string PlayerId, string? Reason, int? DurationMinutes);
public sealed record ConfigFileRequest(string Content);
public sealed record PluginActionRequest(string Path, string Action);
public sealed record PluginConfigRequest(string Path, string Content);
public sealed record ScheduleRequest(
    Guid ServerId, string Name, string Cron, string Action, bool Enabled, int WarningSeconds = 0);
public sealed record BridgePlayerReport(
    int PlayerId, string DisplayName, string UserId, string IpAddress, string Role,
    int Ping = 0, long SessionSeconds = 0, bool IsMuted = false);
public sealed record BridgeHeartbeat(string BridgeVersion, string ApiVersion, string RoundState, int MaxPlayers, IReadOnlyList<BridgePlayerReport> Players);
public sealed record BridgeStatus(bool Connected, DateTimeOffset? LastSeenAt, string? BridgeVersion, string? ApiVersion, string? RoundState, int MaxPlayers, IReadOnlyList<PlayerInfo> Players);
public sealed record PlayerNameEntry(string Name, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);
public sealed record PlayerModerationEntry(
    Guid Id, string Type, string Reason, string Actor, DateTimeOffset At, int? DurationMinutes,
    bool Revoked = false);
public sealed record PlayerNote(Guid Id, string Text, string Actor, DateTimeOffset At);
public sealed record PlayerRecord(
    Guid Id, Guid ServerId, string UserId, string LastIpAddress, string CurrentName,
    DateTimeOffset FirstConnectedAt, DateTimeOffset LastConnectedAt, long PlaytimeSeconds,
    int Connections, IReadOnlyList<PlayerNameEntry> NameHistory,
    IReadOnlyList<PlayerModerationEntry> ModerationHistory, IReadOnlyList<PlayerNote> Notes,
    string? DiscordId = null, string? SteamDisplayName = null, string? SteamAvatarUrl = null,
    string? SteamProfileUrl = null, string? DiscordDisplayName = null,
    string? DiscordAvatarUrl = null, IReadOnlyList<string>? DiscordRoles = null);
public sealed record PlayerNoteRequest(string Text);
public sealed record PlayerActionRequest(string Type, string Reason, int? DurationMinutes);
public sealed record MetricSample(
    Guid ServerId, DateTimeOffset At, double CpuPercent, long MemoryBytes,
    int Players, ServerState State, bool BridgeConnected);
public sealed record ServerIncident(
    Guid Id, Guid ServerId, DateTimeOffset At, string Type, string Message, int? ExitCode);
public sealed record ConsoleLogEntry(DateTimeOffset At, string Stream, string Line);
public sealed record PanelIntegrationSettings(
    string DiscordWebhookUrl = "", bool NotifyCrash = true, bool NotifyRestart = true,
    bool NotifyBridgeOffline = true, bool NotifyAdminActions = false,
    bool NotifyHighCpu = true, double HighCpuPercent = 90,
    bool NotifyHighMemory = true, int HighMemoryMb = 4096, int AlertCooldownMinutes = 15,
    string CrashMessage = "{server} stopped unexpectedly with exit code {exitCode}. Auto-restart is {autoRestart}.",
    string BridgeOfflineMessage = "{server} is online, but its LabAPI bridge stopped responding. Live player data and remote moderation may be unavailable.",
    string HighCpuMessage = "{server} CPU usage is {cpu}% (alert threshold: {threshold}%).",
    string HighMemoryMessage = "{server} memory usage is {memoryMb} MB (alert threshold: {thresholdMb} MB).",
    string RestartFailureMessage = "{server} failed to restart automatically: {error}",
    string ScheduleFailureMessage = "Schedule '{schedule}' failed for {server}: {error}",
    bool DiscordBotEnabled = false, string DiscordBotToken = "",
    [property: JsonConverter(typeof(SnowflakeStringConverter))] string DiscordGuildId = "",
    string DiscordControlRoleIds = "",
    [property: JsonConverter(typeof(SnowflakeStringConverter))] string DiscordNotificationChannelId = "",
    string SteamWebApiKey = "", IReadOnlyList<DiscordRoleGrant>? DiscordRoleGrants = null,
    [property: JsonConverter(typeof(SnowflakeStringConverter))] string DiscordModerationChannelId = "",
    [property: JsonConverter(typeof(SnowflakeStringConverter))] string DiscordAuditChannelId = "",
    bool DiscordDailyReportEnabled = false, int DiscordDailyReportHourUtc = 12,
    IReadOnlyList<DiscordGameRoleGrant>? DiscordGameRoleGrants = null,
    IReadOnlyList<DiscordDonorRoleGrant>? DiscordDonorRoleGrants = null,
    IReadOnlyList<CustomUserBadge>? CustomUserBadges = null);
public sealed record DiscordBotStatus(bool Enabled, bool Connected, string? BotName, string? Error);
public sealed record DiscordChannelDiagnostic(
    string Purpose, string ChannelId, bool Found, string? Name, bool CanView, bool CanSend);
public sealed record DiscordGuildRole(string Id, string Name, int Position, uint Color);
public sealed record DiscordDiagnostic(
    DiscordBotStatus Bot, string GuildId, bool GuildFound, string? GuildName,
    IReadOnlyList<DiscordChannelDiagnostic> Channels, IReadOnlyList<string> Issues);
public sealed record DiscordRoleGrant(
    [property: JsonConverter(typeof(SnowflakeStringConverter))] string RoleId,
    Guid ServerId, IReadOnlyList<string> Permissions);
public sealed record DiscordGameRoleGrant(
    [property: JsonConverter(typeof(SnowflakeStringConverter))] string RoleId,
    Guid ServerId, string GroupName, int Priority = 0, bool Enabled = true,
    IReadOnlyList<string>? Permissions = null, IReadOnlyList<string>? InheritedGroups = null,
    string BadgeText = "", string BadgeColor = "silver", bool Hidden = false,
    bool Cover = true, bool ReservedSlot = false, byte KickPower = 0,
    byte RequiredKickPower = 0, IReadOnlyList<string>? PluginPermissions = null);
public sealed record DiscordDonorRoleGrant(
    [property: JsonConverter(typeof(SnowflakeStringConverter))] string RoleId,
    Guid ServerId, int Tier, int Priority = 0, bool Enabled = true);
public sealed record CustomUserBadge(
    Guid ServerId, string SteamId, string BadgeText, string BadgeColor = "silver");
public sealed record BridgeCustomBadge(bool Assigned, string BadgeText = "", string BadgeColor = "silver");
public sealed record DonorSyncResult(
    Guid ServerId, string ServerName, int Donors, int Added, int Updated, int Removed,
    string Path);
public sealed record BridgeGameRoleAssignment(
    bool Assigned, string? GroupName = null, string? DiscordId = null,
    string? DiscordRoleId = null, IReadOnlyList<string>? Permissions = null,
    string BadgeText = "", string BadgeColor = "silver", bool Hidden = false,
    bool Cover = true, bool ReservedSlot = false, byte KickPower = 0,
    byte RequiredKickPower = 0, IReadOnlyList<string>? PluginPermissions = null);
public sealed record PermissionIssue(
    string Severity, string Code, string Message, Guid? ServerId = null, string? GroupName = null);
public sealed record PermissionRoleRuntime(
    Guid ServerId, string ServerName, string GroupName, string DiscordRoleId, int Priority,
    bool Enabled, int NativePermissionCount, int PluginPermissionCount, int OnlinePlayers,
    bool BridgeConnected, DateTimeOffset? BridgeLastSeenAt);
public sealed record PlayerPermissionDiagnostic(
    Guid ServerId, string ServerName, string UserId, string? DisplayName, bool Online,
    string? CurrentGameRole, BridgeGameRoleAssignment Assignment,
    IReadOnlyList<string> InheritedGroups, IReadOnlyList<PermissionIssue> Issues);
public sealed record PermissionHealth(
    IReadOnlyList<PermissionIssue> Issues, IReadOnlyList<PermissionRoleRuntime> Roles,
    IReadOnlyList<string> NativePermissionCatalog, IReadOnlyList<string> PluginPermissionCatalog);
public sealed record NativeRaComparison(
    Guid ServerId, string ServerName, bool Found, string? Path,
    IReadOnlyList<string> NativeGroups, IReadOnlyList<string> PanelGroups,
    IReadOnlyList<string> NativeMembers, IReadOnlyList<PermissionIssue> Issues);
public sealed record IdentityLinkHealth(
    Guid ServerId, string ServerName, int Line, string SteamId, string DiscordId,
    bool Valid, string? Issue);
public sealed record NotificationDelivery(
    Guid Id, DateTimeOffset At, string Category, string Severity, string Title,
    string Message, string ChannelId, string Status, int Attempts, string? Error);
public sealed record IdentityLinkRequest(string SteamId, string DiscordId);
public sealed record OperationEntry(
    Guid Id, Guid? ServerId, string Type, string Target, string Actor, string Status,
    string Message, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record BackupEntry(
    Guid Id, Guid ServerId, DateTimeOffset CreatedAt, string FileName, long SizeBytes, string Actor);
public sealed record RestartCountdownRequest(int Seconds, string? Message);
public sealed record RestartCountdownStatus(Guid ServerId, DateTimeOffset DueAt, string Message, string Actor);
public sealed record BridgeCommand(
    Guid Id, string Type, string? PlayerId, string? Reason, int? DurationSeconds,
    string? Message, DateTimeOffset CreatedAt);
public sealed record BridgeCommandResult(bool Success, string? Message);
public sealed record BridgeEventRequest(
    string Type, DateTimeOffset At, string? PlayerId = null, string? UserId = null,
    string? DisplayName = null, string? Detail = null, int? DurationSeconds = null,
    string? Actor = null);
public sealed record ServerActivityEntry(
    Guid Id, Guid ServerId, DateTimeOffset At, string Type, string? PlayerId,
    string? UserId, string? DisplayName, string Detail, string? Actor = null,
    int? DurationSeconds = null);
public sealed record RoundHistoryEntry(
    Guid Id, Guid ServerId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    string? LeadingTeam, long? DurationSeconds);
public sealed record AnnouncementRequest(string Message, int DurationSeconds);
