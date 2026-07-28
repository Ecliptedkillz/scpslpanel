namespace ScpSlPanel.Api.Domain;

public enum ServerState { Offline, Starting, Online, Stopping, Faulted }

public sealed record ServerDefinition(
    Guid Id, string Name, string ExecutablePath, string Arguments, string WorkingDirectory,
    bool AutoRestart, bool AutoStart, int QueryPort, string? UpdateCommand, DateTimeOffset CreatedAt,
    string? BridgeToken = null);

public sealed record ServerSnapshot(
    Guid Id, string Name, ServerState State, int? ProcessId, DateTimeOffset? StartedAt,
    long MemoryBytes, double CpuPercent, int Players, int MaxPlayers, string? LastError);

public sealed record PlayerInfo(
    string Id, string Nickname, string UserId, string IpAddress, string Role, int Ping,
    DateTimeOffset ConnectedAt);

public sealed record BanEntry(
    Guid Id, string Target, string DisplayName, string Reason, string IssuedBy,
    DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt, bool Revoked);

public sealed record AuditEntry(
    Guid Id, DateTimeOffset At, string Actor, string Action, string Target, string Detail);

public sealed record ScheduleEntry(
    Guid Id, Guid ServerId, string Name, string Cron, string Action, bool Enabled,
    DateTimeOffset? LastRunAt);

public sealed record PanelUser(
    Guid Id, string Username, string PasswordHash, string Role, bool Enabled,
    DateTimeOffset CreatedAt);

public sealed record PluginEntry(
    string Name, string Version, string Framework, bool Enabled, string Path);

public sealed record DashboardOverview(
    int ServersOnline, int ServersTotal, int PlayersOnline, long MemoryBytes,
    IReadOnlyList<ServerSnapshot> Servers, IReadOnlyList<AuditEntry> RecentActivity);

public sealed record ServerCreateRequest(
    string Name, string ExecutablePath, string? Arguments, string? WorkingDirectory,
    bool AutoRestart, bool AutoStart, int QueryPort, string? UpdateCommand);

public sealed record LoginRequest(string Username, string Password);
public sealed record CommandRequest(string Command);
public sealed record ModerationRequest(string PlayerId, string? Reason, int? DurationMinutes);
public sealed record ConfigFileRequest(string Content);
public sealed record PluginActionRequest(string Path, string Action);
public sealed record PluginConfigRequest(string Path, string Content);
public sealed record ScheduleRequest(Guid ServerId, string Name, string Cron, string Action, bool Enabled);
public sealed record BridgePlayerReport(int PlayerId, string DisplayName, string UserId, string IpAddress, string Role);
public sealed record BridgeHeartbeat(string BridgeVersion, string ApiVersion, string RoundState, int MaxPlayers, IReadOnlyList<BridgePlayerReport> Players);
public sealed record BridgeStatus(bool Connected, DateTimeOffset? LastSeenAt, string? BridgeVersion, string? ApiVersion, string? RoundState, int MaxPlayers, IReadOnlyList<PlayerInfo> Players);
