using System.Collections.Concurrent;
using ScpSlPanel.Api.Domain;

namespace ScpSlPanel.Api.Services;

public sealed class BridgeStateService
{
    private sealed record State(
        DateTimeOffset LastSeenAt, string BridgeVersion, string ApiVersion, string RoundState,
        int MaxPlayers, IReadOnlyList<PlayerInfo> Players);

    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<Guid, State> _states = new();

    public void Update(Guid serverId, BridgeHeartbeat heartbeat)
    {
        var now = DateTimeOffset.UtcNow;
        _states.TryGetValue(serverId, out var previous);
        var players = heartbeat.Players.Select(player =>
        {
            var existing = previous?.Players.FirstOrDefault(item => item.Id == player.PlayerId.ToString());
            return new PlayerInfo(
                player.PlayerId.ToString(),
                player.DisplayName,
                player.UserId,
                player.IpAddress,
                player.Role,
                player.Ping,
                existing?.ConnectedAt ?? now.Subtract(TimeSpan.FromSeconds(Math.Max(0, player.SessionSeconds))),
                player.SessionSeconds,
                player.IsMuted);
        }).ToList();
        _states[serverId] = new State(now, heartbeat.BridgeVersion, heartbeat.ApiVersion,
            heartbeat.RoundState, heartbeat.MaxPlayers, players);
    }

    public BridgeStatus Get(Guid serverId)
    {
        if (!_states.TryGetValue(serverId, out var state))
            return new(false, null, null, null, null, 0, []);
        var connected = DateTimeOffset.UtcNow - state.LastSeenAt <= OnlineWindow;
        return new(connected, state.LastSeenAt, state.BridgeVersion, state.ApiVersion,
            state.RoundState, state.MaxPlayers, connected ? state.Players : []);
    }
}
