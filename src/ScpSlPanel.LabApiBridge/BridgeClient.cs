using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;

namespace ScpSlPanel.LabApiBridge;

internal sealed class BridgeClient : IDisposable
{
    private readonly BridgeConfig _config;
    private readonly HttpClient _http = new();
    private readonly Timer _timer;
    private readonly Timer _commandTimer;
    private readonly Timer _roleSyncTimer;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly object _snapshotGate = new();
    private readonly SynchronizationContext? _mainThread = SynchronizationContext.Current;
    private readonly Dictionary<int, DateTimeOffset> _sessions = new();
    private readonly HashSet<Guid> _receivedCommands = new();
    private readonly HashSet<string> _assignedGameRoleUsers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _assignedCustomBadgeUsers =
        new(StringComparer.OrdinalIgnoreCase);
    private string _roundState = "waiting";
    private HeartbeatPayload _snapshot = new();
    private bool _disposed;

    public BridgeClient(BridgeConfig config)
    {
        _config = config;
        _http.Timeout = TimeSpan.FromSeconds(8);
        var interval = TimeSpan.FromSeconds(Math.Max(2, config.HeartbeatSeconds));
        // LabAPI enables plugins before every game wrapper is guaranteed to be ready.
        // Defer the first snapshot instead of touching Round/Player state in the constructor.
        _timer = new Timer(_ => Dispatch(CaptureSnapshotSafely), null, TimeSpan.FromSeconds(2), interval);
        _commandTimer = new Timer(_ => _ = PollCommandsAsync(), null,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
        _roleSyncTimer = new Timer(_ => Dispatch(SynchronizeReadyPlayers), null,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
    }

    private void CaptureSnapshotSafely()
    {
        try
        {
            CaptureSnapshot();
        }
        catch (Exception exception)
        {
            Logger.Warn($"SCP Control snapshot failed unexpectedly: {exception}");
        }
    }

    private void SynchronizeReadyPlayers()
    {
        if (_disposed) return;
        try
        {
            foreach (var player in Player.ReadyList.Where(player => player != null && !player.IsHost))
                CheckDiscordGameRole(player);
        }
        catch (Exception exception)
        {
            Logger.Warn($"SCP Control periodic role synchronization failed: {exception.Message}");
        }
    }

    public void CaptureSnapshot(Player? excluded = null)
    {
        var now = DateTimeOffset.UtcNow;
        var activeIds = new HashSet<int>();
        Player[] readyPlayers;
        try
        {
            readyPlayers = Player.ReadyList?.ToArray() ?? Array.Empty<Player>();
        }
        catch (Exception exception)
        {
            Logger.Warn($"SCP Control could not read the player list yet: {exception.Message}");
            readyPlayers = Array.Empty<Player>();
        }

        var players = new List<PlayerPayload>();
        foreach (var player in readyPlayers.Where(player => player != null && !player.IsHost && player != excluded))
        {
            try
            {
                activeIds.Add(player.PlayerId);
                if (!_sessions.TryGetValue(player.PlayerId, out var connectedAt))
                    _sessions[player.PlayerId] = connectedAt = now;
                var hideIdentity = _config.RespectDoNotTrack && player.DoNotTrack;
                players.Add(new PlayerPayload
                {
                    PlayerId = player.PlayerId,
                    DisplayName = player.DisplayName ?? "Unknown",
                    UserId = hideIdentity ? "" : player.UserId ?? "",
                    IpAddress = hideIdentity ? "" : player.IpAddress ?? "",
                    Role = player.Role.ToString(),
                    Ping = ReadPing(player),
                    SessionSeconds = Math.Max(0, (long)(now - connectedAt).TotalSeconds),
                    IsMuted = player.IsMuted,
                });
            }
            catch (Exception exception)
            {
                Logger.Warn($"SCP Control skipped an unavailable player snapshot: {exception.Message}");
            }
        }
        foreach (var id in _sessions.Keys.Where(id => !activeIds.Contains(id)).ToList()) _sessions.Remove(id);

        var maxPlayers = 0;
        try { maxPlayers = CustomNetworkManager.slots; }
        catch (Exception exception)
        {
            Logger.Warn($"SCP Control could not read the slot count yet: {exception.Message}");
        }

        var snapshot = new HeartbeatPayload
        {
            BridgeVersion = typeof(BridgePlugin).Assembly.GetName().Version.ToString(),
            ApiVersion = LabApiProperties.CompiledVersion,
            RoundState = _roundState,
            MaxPlayers = maxPlayers,
            Players = players,
        };
        lock (_snapshotGate) _snapshot = snapshot;
            _ = SendAsync();
    }

    public void SetRoundState(string state)
    {
        _roundState = state;
        CaptureSnapshot();
    }

    public void RecordEvent(string type, Player? player = null, string? detail = null, int? durationSeconds = null)
    {
        var hideIdentity = player != null && _config.RespectDoNotTrack && player.DoNotTrack;
        _ = PostJsonAsync("events", new EventPayload
        {
            Type = type,
            At = DateTimeOffset.UtcNow.ToString("O"),
            PlayerId = player?.PlayerId.ToString(),
            UserId = hideIdentity ? "" : player?.UserId,
            DisplayName = player?.DisplayName,
            Detail = detail,
            DurationSeconds = durationSeconds,
        }, typeof(EventPayload));
    }

    public void RecordModerationEvent(
        string type, Player? player, string? targetId, string? displayName,
        string? reason, int? durationSeconds, string? actor)
    {
        var hideIdentity = player != null && _config.RespectDoNotTrack && player.DoNotTrack;
        _ = PostJsonAsync("events", new EventPayload
        {
            Type = type,
            At = DateTimeOffset.UtcNow.ToString("O"),
            PlayerId = player?.PlayerId.ToString(),
            UserId = hideIdentity ? "" : (player?.UserId ?? targetId),
            DisplayName = player?.DisplayName ?? displayName ?? targetId,
            Detail = reason,
            DurationSeconds = durationSeconds,
            Actor = actor,
        }, typeof(EventPayload));
    }

    public void RecordReport(Player reporter, Player target, string reason)
    {
        var hideReporter = _config.RespectDoNotTrack && reporter.DoNotTrack;
        var hideTarget = _config.RespectDoNotTrack && target.DoNotTrack;
        _ = PostJsonAsync("events", new EventPayload
        {
            Type = "report",
            At = DateTimeOffset.UtcNow.ToString("O"),
            PlayerId = reporter.PlayerId.ToString(),
            UserId = hideReporter ? "" : reporter.UserId,
            DisplayName = reporter.DisplayName,
            TargetUserId = hideTarget ? "" : target.UserId,
            TargetDisplayName = target.DisplayName,
            Detail = string.IsNullOrWhiteSpace(reason) ? "No reason provided" : reason.Trim(),
        }, typeof(EventPayload));
    }

    public void CheckPanelBan(Player player)
    {
        if (player == null || player.IsHost) return;
        var playerId = player.PlayerId;
        var userId = player.UserId ?? "";
        var ipAddress = player.IpAddress ?? "";
        _ = CheckPanelBanAsync(playerId, userId, ipAddress);
    }

    private async Task CheckPanelBanAsync(int playerId, string userId, string ipAddress)
    {
        try
        {
            var suffix = $"ban-check?userId={Uri.EscapeDataString(userId)}&ipAddress={Uri.EscapeDataString(ipAddress)}";
            using var request = Authorized(HttpMethod.Get, Endpoint(suffix));
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"SCP Control ban check rejected: {(int)response.StatusCode} {response.ReasonPhrase}");
                return;
            }

            var serializer = new DataContractJsonSerializer(typeof(BanCheckPayload));
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var result = serializer.ReadObject(stream) as BanCheckPayload;
            if (result?.Banned != true) return;

            Dispatch(() =>
            {
                var current = Player.ReadyList?.FirstOrDefault(candidate => candidate.PlayerId == playerId);
                if (current == null) return;
                var reason = string.IsNullOrWhiteSpace(result.Reason)
                    ? "You are banned from this server." : $"Banned: {result.Reason}";
                current.Kick(reason);
                RecordModerationEvent("ban-enforced", current, userId, current.DisplayName,
                    result.Reason, null, "SCP Control");
            });
        }
        catch (Exception exception)
        {
            Logger.Warn($"SCP Control ban check failed: {exception.Message}");
        }
    }

    public void CheckDiscordGameRole(Player player)
    {
        if (player == null || player.IsHost) return;
        _ = CheckDiscordGameRoleWhenReadyAsync(player.PlayerId);
    }

    private async Task CheckDiscordGameRoleWhenReadyAsync(int playerId)
    {
        // PlayerJoined can fire before Steam authentication and the native RA Members
        // assignment have completed. Resolve the ready player and apply the panel role
        // afterwards so Discord-synchronized permissions are authoritative.
        for (var attempt = 0; attempt < 8 && !_disposed; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(1000).ConfigureAwait(false);

            Player? current = null;
            try
            {
                current = Player.ReadyList?.FirstOrDefault(candidate =>
                    candidate.PlayerId == playerId);
            }
            catch
            {
                // The ready list can be unavailable while the connection initializes.
            }

            if (current == null || string.IsNullOrWhiteSpace(current.UserId)) continue;

            // Give SCP:SL's native permissions handler time to finish applying the
            // static Members entry before the panel replaces it.
            await Task.Delay(750).ConfigureAwait(false);
            await CheckDiscordGameRoleAsync(playerId, current.UserId).ConfigureAwait(false);
            return;
        }

        Logger.Warn($"SCP Control could not synchronize an in-game role for player {playerId}: the authenticated player was not ready.");
    }

    private async Task CheckDiscordGameRoleAsync(int playerId, string userId)
    {
        try
        {
            var suffix = $"game-role?userId={Uri.EscapeDataString(userId)}";
            using var request = Authorized(HttpMethod.Get, Endpoint(suffix));
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"SCP Control game-role check rejected: {(int)response.StatusCode} {response.ReasonPhrase}");
                return;
            }
            var serializer = new DataContractJsonSerializer(typeof(GameRolePayload));
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var assignment = serializer.ReadObject(stream) as GameRolePayload;
            if (assignment?.Assigned != true || string.IsNullOrWhiteSpace(assignment.GroupName))
            {
                Dispatch(() =>
                {
                    var current = Player.ReadyList?.FirstOrDefault(candidate =>
                        candidate.PlayerId == playerId);
                    if (current == null) return;
                    if (!_assignedGameRoleUsers.Remove(current.UserId)) return;
                    PanelPermissionProvider.Clear(current.UserId);
                    PanelRainbowTag.Detach(current);
                    current.UserGroup = ServerStatic.PermissionsHandler.GetUserGroup(current.UserId);
                    Logger.Info($"SCP Control removed its runtime role from {current.UserId}; native permissions were restored.");
                    RecordEvent("role-sync", current, "Removed panel role; restored native permissions");
                });
                return;
            }
            Dispatch(() =>
            {
                var current = Player.ReadyList?.FirstOrDefault(candidate => candidate.PlayerId == playerId);
                if (current == null) return;
                PanelPermissionProvider.Set(current.UserId, assignment.PluginPermissions);
                if (!TryApplyRemoteAdminGroup(current, assignment, out var error))
                {
                    Logger.Warn($"SCP Control could not assign RA group '{assignment.GroupName}': {error}");
                    return;
                }
                PanelRainbowTag.Attach(current);
                _assignedGameRoleUsers.Add(current.UserId);
                Logger.Info($"SCP Control assigned RA group '{assignment.GroupName}' to {current.UserId} from Discord role {assignment.DiscordRoleId}.");
                RecordModerationEvent("role-sync", current, current.UserId, current.DisplayName,
                    $"Assigned in-game group {assignment.GroupName}", null, "Discord role sync");
            });
        }
        catch (Exception exception)
        {
            Logger.Warn($"SCP Control game-role check failed: {exception.Message}");
        }
        finally
        {
            await CheckCustomBadgeAsync(playerId, userId).ConfigureAwait(false);
        }
    }

    private async Task CheckCustomBadgeAsync(int playerId, string userId)
    {
        try
        {
            using var request = Authorized(HttpMethod.Get,
                Endpoint($"custom-badge?userId={Uri.EscapeDataString(userId)}"));
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            var serializer = new DataContractJsonSerializer(typeof(CustomBadgePayload));
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var badge = serializer.ReadObject(stream) as CustomBadgePayload;
            Dispatch(() =>
            {
                var current = Player.ReadyList?.FirstOrDefault(candidate => candidate.PlayerId == playerId);
                if (current == null) return;
                if (badge?.Assigned == true && !string.IsNullOrWhiteSpace(badge.BadgeText))
                {
                    if (TrySetCustomBadge(current, badge.BadgeText, badge.BadgeColor))
                        _assignedCustomBadgeUsers.Add(current.UserId);
                }
                else if (_assignedCustomBadgeUsers.Remove(current.UserId))
                    RefreshBadge(current);
            });
        }
        catch (Exception exception)
        {
            Logger.Warn($"SCP Control custom-badge check failed: {exception.Message}");
        }
    }

    private static bool TrySetCustomBadge(Player player, string text, string color)
    {
        var roles = GetServerRoles(player);
        if (roles == null) return false;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        roles.GetType().GetMethod("SetText", flags, null, new[] { typeof(string) }, null)?.Invoke(roles, new object[] { text });
        roles.GetType().GetMethod("SetColor", flags, null, new[] { typeof(string) }, null)?.Invoke(roles, new object[] { color });
        return true;
    }

    private static void RefreshBadge(Player player)
    {
        var roles = GetServerRoles(player);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        roles?.GetType().GetMethods(flags).FirstOrDefault(method =>
            method.Name == "RefreshPermissions" && method.GetParameters().Length <= 1)
            ?.Invoke(roles, roles.GetType().GetMethods(flags).First(method =>
                method.Name == "RefreshPermissions" && method.GetParameters().Length <= 1)
                .GetParameters().Length == 0 ? null : new object[] { true });
    }

    private static object? GetServerRoles(Player player)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var hub = player.GetType().GetProperty("ReferenceHub", flags)?.GetValue(player);
        return hub?.GetType().GetField("serverRoles", flags)?.GetValue(hub)
            ?? hub?.GetType().GetProperty("serverRoles", flags)?.GetValue(hub);
    }

    private static bool TryApplyRemoteAdminGroup(Player player, GameRolePayload assignment, out string error)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static;
            var hub = player.GetType().GetProperty("ReferenceHub", flags)?.GetValue(player);
            if (hub == null) { error = "Player ReferenceHub is unavailable."; return false; }
            var roles = hub.GetType().GetField("serverRoles", flags)?.GetValue(hub)
                ?? hub.GetType().GetProperty("serverRoles", flags)?.GetValue(hub);
            if (roles == null) { error = "ServerRoles is unavailable."; return false; }
            var serverStatic = Type.GetType("ServerStatic, Assembly-CSharp");
            var handler = serverStatic?.GetField("PermissionsHandler", flags)?.GetValue(null)
                ?? serverStatic?.GetProperty("PermissionsHandler", flags)?.GetValue(null);
            if (handler == null) { error = "PermissionsHandler is unavailable."; return false; }
            var getGroup = handler.GetType().GetMethods(flags).FirstOrDefault(method =>
                method.Name == "GetGroup" && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == typeof(string));
            if (getGroup == null) { error = "PermissionsHandler.GetGroup is unavailable."; return false; }
            var groupType = getGroup.ReturnType;
            var group = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(groupType);
            var gameAssembly = serverStatic!.Assembly;
            Type[] gameTypes;
            try { gameTypes = gameAssembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception)
            {
                gameTypes = exception.Types.Where(type => type != null).Cast<Type>().ToArray();
            }
            var permissionEnum = gameTypes.FirstOrDefault(type => type.IsEnum
                && Enum.GetNames(type).Contains("KickingAndShortTermBanning"));
            if (permissionEnum == null) { error = "SCP:SL permission enum is unavailable."; return false; }
            ulong permissionMask = 0;
            foreach (var permission in assignment.Permissions)
            {
                try { permissionMask |= Convert.ToUInt64(Enum.Parse(permissionEnum, permission, true)); }
                catch { Logger.Warn($"SCP Control ignored unknown RA permission '{permission}'."); }
            }
            SetGroupMember(group, "Permissions", permissionMask);
            SetGroupMember(group, "KickPower", assignment.KickPower);
            SetGroupMember(group, "RequiredKickPower", assignment.RequiredKickPower);
            SetGroupMember(group, "BadgeText", assignment.BadgeText);
            SetGroupMember(group, "BadgeColor", assignment.BadgeColor);
            SetGroupMember(group, "Cover", assignment.Cover);
            SetGroupMember(group, "HiddenByDefault", assignment.Hidden);
            SetGroupMember(group, "Hidden", assignment.Hidden);
            SetGroupMember(group, "ReservedSlot", assignment.ReservedSlot);
            SetGroupMember(group, "Name", assignment.GroupName!);

            // Register the dynamic role under its panel rank name. Some LabAPI
            // plugins resolve features (for example animated tags) by looking up
            // ServerStatic.PermissionsHandler.Groups rather than reading the
            // currently displayed badge from ServerRoles.
            var groups = handler.GetType().GetField("Groups", flags)?.GetValue(handler)
                ?? handler.GetType().GetProperty("Groups", flags)?.GetValue(handler);
            if (groups is IDictionary groupDictionary)
                groupDictionary[assignment.GroupName!] = group;

            var setGroup = roles.GetType().GetMethods(flags).FirstOrDefault(method =>
            {
                var parameters = method.GetParameters();
                return method.Name == "SetGroup" && parameters.Length >= 1
                    && parameters[0].ParameterType.IsInstanceOfType(group);
            });
            if (setGroup == null) { error = "ServerRoles.SetGroup is unavailable."; return false; }
            var parameters = setGroup.GetParameters();
            var arguments = new object?[parameters.Length];
            arguments[0] = group;
            for (var index = 1; index < parameters.Length; index++)
                arguments[index] = parameters[index].HasDefaultValue
                    ? parameters[index].DefaultValue
                    : parameters[index].ParameterType == typeof(bool) ? index == parameters.Length - 1
                    : Activator.CreateInstance(parameters[index].ParameterType);
            setGroup.Invoke(roles, arguments);
            error = "";
            return true;
        }
        catch (Exception exception)
        {
            error = exception.InnerException?.Message ?? exception.Message;
            return false;
        }
    }

    private static void SetGroupMember(object group, string name, object value)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = group.GetType();
        var property = type.GetProperties(flags).FirstOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && item.CanWrite);
        if (property != null)
        {
            property.SetValue(group, ConvertValue(value, property.PropertyType));
            return;
        }
        var field = type.GetFields(flags).FirstOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || item.Name.Equals($"<{name}>k__BackingField", StringComparison.OrdinalIgnoreCase));
        if (field != null) field.SetValue(group, ConvertValue(value, field.FieldType));
    }

    private static object ConvertValue(object value, Type targetType) =>
        targetType.IsEnum ? Enum.ToObject(targetType, Convert.ToUInt64(value))
        : Convert.ChangeType(value, Nullable.GetUnderlyingType(targetType) ?? targetType);

    private async Task PollCommandsAsync()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_config.PanelUrl)) return;
        if (!await _pollGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var endpoint = Endpoint("commands");
            using var request = Authorized(HttpMethod.Get, endpoint);
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            var serializer = new DataContractJsonSerializer(typeof(List<CommandPayload>));
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var commands = (List<CommandPayload>?)serializer.ReadObject(stream) ?? new();
            foreach (var command in commands)
            {
                lock (_receivedCommands)
                    if (!_receivedCommands.Add(command.Id)) continue;
                Dispatch(() => ExecuteCommand(command));
            }
        }
        catch (Exception exception) { Logger.Warn($"SCP Control command poll failed: {exception.Message}"); }
        finally { _pollGate.Release(); }
    }

    private void ExecuteCommand(CommandPayload command)
    {
        var result = new CommandResultPayload();
        try
        {
            var player = string.IsNullOrWhiteSpace(command.PlayerId) ? null
                : Player.ReadyList.FirstOrDefault(x => x.PlayerId.ToString() == command.PlayerId
                    || x.UserId == command.PlayerId);
            switch (command.Type.ToLowerInvariant())
            {
                case "kick":
                    result.Success = player != null && player.Kick(command.Reason ?? "Removed by SCP Control");
                    result.Message = player == null ? "Player is no longer connected." : result.Success ? "Kick confirmed." : "Kick was rejected.";
                    break;
                case "ban":
                    result.Success = player != null && player.Kick(command.Reason ?? "Banned by SCP Control");
                    result.Message = player == null ? "Player is no longer connected."
                        : result.Success ? "Panel ban saved and player removed." : "Player removal was rejected.";
                    break;
                case "mute":
                    if (player == null) { result.Message = "Player is no longer connected."; break; }
                    player.Mute(true);
                    result.Success = player.IsMuted;
                    result.Message = result.Success ? "Mute confirmed." : "Mute could not be verified.";
                    break;
                case "unmute":
                    if (player == null) { result.Message = "Player is no longer connected."; break; }
                    player.Unmute(true);
                    result.Success = !player.IsMuted;
                    result.Message = result.Success ? "Unmute confirmed." : "Unmute could not be verified.";
                    break;
                case "announcement":
                    Server.SendBroadcast(command.Message ?? "", (ushort)Math.Max(1, Math.Min(ushort.MaxValue, command.DurationSeconds ?? 10)));
                    result.Success = true;
                    result.Message = "Announcement sent.";
                    break;
                case "role-sync":
                    if (player == null) { result.Message = "Player is no longer connected."; break; }
                    CheckDiscordGameRole(player);
                    result.Success = true;
                    result.Message = "Permission synchronization queued.";
                    break;
                default:
                    result.Message = $"Unsupported command: {command.Type}";
                    break;
            }
        }
        catch (Exception exception) { result.Message = exception.Message; }
        _ = AcknowledgeAsync(command.Id, result);
        CaptureSnapshot();
    }

    private async Task AcknowledgeAsync(Guid commandId, CommandResultPayload result)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await PostJsonAsync($"commands/{commandId}/result", result, typeof(CommandResultPayload))) break;
            await Task.Delay(1000).ConfigureAwait(false);
        }
        lock (_receivedCommands) _receivedCommands.Remove(commandId);
    }

    private void Dispatch(Action action)
    {
        if (_mainThread == null) action();
        else _mainThread.Post(_ => action(), null);
    }

    private static int ReadPing(Player player)
    {
        try
        {
            var connection = player.ConnectionToClient;
            var property = connection.GetType().GetProperty("rtt");
            var value = property?.GetValue(connection);
            return value is double seconds ? Math.Max(0, (int)Math.Round(seconds * 1000))
                : value is float floatSeconds ? Math.Max(0, (int)Math.Round(floatSeconds * 1000)) : 0;
        }
        catch { return 0; }
    }

    private string Endpoint(string suffix) =>
        $"{_config.PanelUrl.TrimEnd('/')}/api/bridge/{Uri.EscapeDataString(_config.ServerId)}/{suffix}";

    private HttpRequestMessage Authorized(HttpMethod method, string endpoint)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.TryAddWithoutValidation("X-Bridge-Token", _config.Token);
        return request;
    }

    private async Task<bool> PostJsonAsync(string suffix, object value, Type type)
    {
        try
        {
            var serializer = new DataContractJsonSerializer(type);
            byte[] body;
            using (var stream = new MemoryStream()) { serializer.WriteObject(stream, value); body = stream.ToArray(); }
            using var request = Authorized(HttpMethod.Post, Endpoint(suffix));
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                Logger.Warn($"SCP Control bridge request rejected: {(int)response.StatusCode} {response.ReasonPhrase}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) { Logger.Warn($"SCP Control bridge request failed: {exception.Message}"); return false; }
    }

    private async Task SendAsync()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_config.PanelUrl)
            || string.IsNullOrWhiteSpace(_config.ServerId) || string.IsNullOrWhiteSpace(_config.Token))
            return;
        if (!await _sendGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            HeartbeatPayload snapshot;
            lock (_snapshotGate) snapshot = _snapshot;
            var serializer = new DataContractJsonSerializer(typeof(HeartbeatPayload));
            byte[] body;
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, snapshot);
                body = stream.ToArray();
            }
            var endpoint = $"{_config.PanelUrl.TrimEnd('/')}/api/bridge/{Uri.EscapeDataString(_config.ServerId)}/heartbeat";
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.TryAddWithoutValidation("X-Bridge-Token", _config.Token);
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using (var response = await _http.SendAsync(request).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        Logger.Warn($"SCP Control heartbeat rejected: {(int)response.StatusCode} {response.ReasonPhrase}");
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Warn($"SCP Control heartbeat failed: {exception.Message}");
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _commandTimer.Dispose();
        _roleSyncTimer.Dispose();
        _http.Dispose();
        _sendGate.Dispose();
        _pollGate.Dispose();
    }
}
