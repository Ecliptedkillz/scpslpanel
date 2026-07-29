using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed class PlayerDataService(JsonStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RecordHeartbeatAsync(Guid serverId, BridgeHeartbeat heartbeat)
    {
        await _gate.WaitAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var records = await store.ReadAsync<PlayerRecord>("players");
            foreach (var report in heartbeat.Players)
            {
                var identity = StableIdentity(report);
                if (identity is null) continue;
                var index = records.FindIndex(x => x.ServerId == serverId
                    && x.UserId.Equals(identity, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    records.Add(new PlayerRecord(Guid.NewGuid(), serverId, identity, report.IpAddress,
                        report.DisplayName, now, now, 0, 1,
                        [new PlayerNameEntry(report.DisplayName, now, now)], [], []));
                    continue;
                }

                var record = records[index];
                var names = record.NameHistory.ToList();
                var nameIndex = names.FindIndex(x => x.Name.Equals(report.DisplayName, StringComparison.Ordinal));
                if (nameIndex < 0) names.Add(new(report.DisplayName, now, now));
                else names[nameIndex] = names[nameIndex] with { LastSeenAt = now };
                var elapsed = Math.Clamp((long)(now - record.LastConnectedAt).TotalSeconds, 0, 15);
                var newSession = now - record.LastConnectedAt > TimeSpan.FromSeconds(30);
                records[index] = record with
                {
                    CurrentName = report.DisplayName,
                    LastIpAddress = string.IsNullOrWhiteSpace(report.IpAddress) ? record.LastIpAddress : report.IpAddress,
                    LastConnectedAt = now,
                    PlaytimeSeconds = record.PlaytimeSeconds + elapsed,
                    Connections = record.Connections + (newSession ? 1 : 0),
                    NameHistory = names
                };
            }
            await store.WriteAsync("players", records);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<PlayerRecord>> ListAsync(Guid serverId)
    {
        var values = await store.ReadAsync<PlayerRecord>("players");
        return values.Where(x => x.ServerId == serverId)
            .OrderByDescending(x => x.LastConnectedAt).ToList();
    }

    public async Task<PlayerRecord?> FindAsync(Guid serverId, Guid playerId) =>
        (await store.ReadAsync<PlayerRecord>("players"))
            .FirstOrDefault(x => x.ServerId == serverId && x.Id == playerId);

    public async Task RecordModerationAsync(
        Guid serverId, string identity, string displayName, string type, string reason,
        string actor, int? durationMinutes)
    {
        await _gate.WaitAsync();
        try
        {
            var records = await store.ReadAsync<PlayerRecord>("players");
            static string Normalize(string value) => value.Split('@')[0].Trim();
            var index = records.FindIndex(x => x.ServerId == serverId
                && (x.UserId.Equals(identity, StringComparison.OrdinalIgnoreCase)
                    || Normalize(x.UserId).Equals(Normalize(identity), StringComparison.OrdinalIgnoreCase)));
            if (index < 0) return;
            var record = records[index];
            if (record.ModerationHistory.Any(x => x.Type == type && x.Reason == reason
                && DateTimeOffset.UtcNow - x.At < TimeSpan.FromSeconds(10)))
                return;
            records[index] = record with
            {
                CurrentName = displayName,
                ModerationHistory = record.ModerationHistory
                    .Append(new PlayerModerationEntry(Guid.NewGuid(), type, reason, actor,
                        DateTimeOffset.UtcNow, durationMinutes)).ToList()
            };
            await store.WriteAsync("players", records);
        }
        finally { _gate.Release(); }
    }

    public async Task<PlayerRecord?> AddNoteAsync(Guid serverId, Guid playerId, string text, string actor)
    {
        await _gate.WaitAsync();
        try
        {
            var records = await store.ReadAsync<PlayerRecord>("players");
            var index = records.FindIndex(x => x.ServerId == serverId && x.Id == playerId);
            if (index < 0) return null;
            records[index] = records[index] with
            {
                Notes = records[index].Notes.Append(
                    new PlayerNote(Guid.NewGuid(), text.Trim(), actor, DateTimeOffset.UtcNow)).ToList()
            };
            await store.WriteAsync("players", records);
            return records[index];
        }
        finally { _gate.Release(); }
    }

    public async Task<PlayerRecord?> AddActionAsync(
        Guid serverId, Guid playerId, string type, string reason, string actor, int? durationMinutes)
    {
        await _gate.WaitAsync();
        try
        {
            var records = await store.ReadAsync<PlayerRecord>("players");
            var index = records.FindIndex(x => x.ServerId == serverId && x.Id == playerId);
            if (index < 0) return null;
            records[index] = records[index] with
            {
                ModerationHistory = records[index].ModerationHistory.Append(
                    new PlayerModerationEntry(Guid.NewGuid(), type, reason.Trim(), actor,
                        DateTimeOffset.UtcNow, durationMinutes)).ToList()
            };
            await store.WriteAsync("players", records);
            return records[index];
        }
        finally { _gate.Release(); }
    }

    public async Task<PlayerRecord?> RevokeActionAsync(Guid serverId, Guid playerId, Guid actionId)
    {
        await _gate.WaitAsync();
        try
        {
            var records = await store.ReadAsync<PlayerRecord>("players");
            var index = records.FindIndex(x => x.ServerId == serverId && x.Id == playerId);
            if (index < 0) return null;
            var history = records[index].ModerationHistory.ToList();
            var actionIndex = history.FindIndex(x => x.Id == actionId);
            if (actionIndex < 0) return null;
            history[actionIndex] = history[actionIndex] with { Revoked = true };
            records[index] = records[index] with { ModerationHistory = history };
            await store.WriteAsync("players", records);
            return records[index];
        }
        finally { _gate.Release(); }
    }

    public async Task<int> CleanupAsync(Guid serverId, int olderThanDays)
    {
        await _gate.WaitAsync();
        try
        {
            var records = await store.ReadAsync<PlayerRecord>("players");
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(olderThanDays, 30, 3650));
            var removed = records.RemoveAll(x => x.ServerId == serverId && x.LastConnectedAt < cutoff
                && x.Notes.Count == 0 && x.ModerationHistory.Count == 0);
            if (removed > 0) await store.WriteAsync("players", records);
            return removed;
        }
        finally { _gate.Release(); }
    }

    private static string? StableIdentity(BridgePlayerReport player) =>
        !string.IsNullOrWhiteSpace(player.UserId) ? player.UserId
        : !string.IsNullOrWhiteSpace(player.IpAddress) ? $"ip:{player.IpAddress}"
        : null;
}
