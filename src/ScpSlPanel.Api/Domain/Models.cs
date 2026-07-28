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
    DateTimeOffset ConnectedAt, long SessionSeconds = 0, bool IsMuted = false);

public sealed record BanEntry(
    Guid Id, string Target, string DisplayName, string Reason, string IssuedBy,
    DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt, bool Revoked);

public sealed record AuditEntry(
    Guid Id, DateTimeOffset At, string Actor, string Action, string Target, string Detail);

public sealed record ScheduleEntry(
    Guid Id, Guid ServerId, string Name, string Cron, string Action, bool Enabled,
    DateTimeOffset? LastRunAt, int WarningSeconds = 0);

public sealed record PanelUser(
    Guid Id, string Username, string PasswordHash, string Role, bool Enabled,
    DateTimeOffset CreatedAt, IReadOnlyList<Guid>? ServerIds = null,
    IReadOnlyList<string>? Permissions = null, IReadOnlyList<ServerAccessGrant>? ServerAccess = null);
public sealed record ServerAccessGrant(Guid ServerId, IReadOnlyList<string> Permissions);

public sealed record PluginEntry(
    string Name, string Version, string Framework, bool Enabled, string Path);

public sealed record DashboardOverview(
    int ServersOnline, int ServersTotal, int PlayersOnline, long MemoryBytes,
    IReadOnlyList<ServerSnapshot> Servers, IReadOnlyList<AuditEntry> RecentActivity);

public sealed record ServerCreateRequest(
    string Name, string ExecutablePath, string? Arguments, string? WorkingDirectory,
    bool AutoRestart, bool AutoStart, int QueryPort, string? UpdateCommand);

public sealed record LoginRequest(string Username, string Password);
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
    Guid Id, string Type, string Reason, string Actor, DateTimeOffset At, int? DurationMinutes);
public sealed record PlayerNote(Guid Id, string Text, string Actor, DateTimeOffset At);
public sealed record PlayerRecord(
    Guid Id, Guid ServerId, string UserId, string LastIpAddress, string CurrentName,
    DateTimeOffset FirstConnectedAt, DateTimeOffset LastConnectedAt, long PlaytimeSeconds,
    int Connections, IReadOnlyList<PlayerNameEntry> NameHistory,
    IReadOnlyList<PlayerModerationEntry> ModerationHistory, IReadOnlyList<PlayerNote> Notes,
    string? DiscordId = null);
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
    bool DiscordBotEnabled = false, string DiscordBotToken = "", ulong DiscordGuildId = 0,
    string DiscordControlRoleIds = "");
public sealed record DiscordBotStatus(bool Enabled, bool Connected, string? BotName, string? Error);
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
    string? DisplayName = null, string? Detail = null, int? DurationSeconds = null);
public sealed record ServerActivityEntry(
    Guid Id, Guid ServerId, DateTimeOffset At, string Type, string? PlayerId,
    string? UserId, string? DisplayName, string Detail);
public sealed record RoundHistoryEntry(
    Guid Id, Guid ServerId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    string? LeadingTeam, long? DurationSeconds);
public sealed record AnnouncementRequest(string Message, int DurationSeconds);
