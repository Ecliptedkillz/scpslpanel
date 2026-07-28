using System;
using System.IO;
using System.Linq;
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
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _snapshotGate = new();
    private readonly SynchronizationContext? _mainThread = SynchronizationContext.Current;
    private readonly Dictionary<int, DateTimeOffset> _sessions = new();
    private readonly HashSet<Guid> _receivedCommands = new();
    private string _roundState = "waiting";
    private HeartbeatPayload _snapshot = new();
    private bool _disposed;

    public BridgeClient(BridgeConfig config)
    {
        _config = config;
        _http.Timeout = TimeSpan.FromSeconds(8);
        _roundState = Round.IsRoundInProgress ? "active" : Round.IsRoundEnded ? "ended" : "waiting";
        CaptureSnapshot();
        var interval = TimeSpan.FromSeconds(Math.Max(2, config.HeartbeatSeconds));
        _timer = new Timer(_ => Dispatch(() => CaptureSnapshot()), null, TimeSpan.Zero, interval);
    }

    public void CaptureSnapshot(Player? excluded = null)
    {
        var now = DateTimeOffset.UtcNow;
        var activeIds = new HashSet<int>();
        var players = Player.ReadyList
            .Where(player => !player.IsHost && player != excluded)
            .Select(player =>
            {
                activeIds.Add(player.PlayerId);
                if (!_sessions.TryGetValue(player.PlayerId, out var connectedAt))
                    _sessions[player.PlayerId] = connectedAt = now;
                var hideIdentity = _config.RespectDoNotTrack && player.DoNotTrack;
                return new PlayerPayload
                {
                    PlayerId = player.PlayerId,
                    DisplayName = player.DisplayName ?? "Unknown",
                    UserId = hideIdentity ? "" : player.UserId ?? "",
                    IpAddress = hideIdentity ? "" : player.IpAddress ?? "",
                    Role = player.Role.ToString(),
                    Ping = ReadPing(player),
                    SessionSeconds = Math.Max(0, (long)(now - connectedAt).TotalSeconds),
                    IsMuted = player.IsMuted,
                };
            }).ToList();
        foreach (var id in _sessions.Keys.Where(id => !activeIds.Contains(id)).ToList()) _sessions.Remove(id);

        var snapshot = new HeartbeatPayload
        {
            BridgeVersion = typeof(BridgePlugin).Assembly.GetName().Version.ToString(),
            ApiVersion = LabApiProperties.CompiledVersion,
            RoundState = _roundState,
            MaxPlayers = CustomNetworkManager.slots,
            Players = players,
        };
        lock (_snapshotGate) _snapshot = snapshot;
            _ = SendAsync();
            _ = PollCommandsAsync();
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

    private async Task PollCommandsAsync()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_config.PanelUrl)) return;
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
                    result.Success = player != null && player.Ban(command.Reason ?? "Banned by SCP Control", Math.Max(1, command.DurationSeconds ?? 3600));
                    result.Message = player == null ? "Player is no longer connected." : result.Success ? "Ban confirmed." : "Ban was rejected.";
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
        _http.Dispose();
        _sendGate.Dispose();
    }
}
