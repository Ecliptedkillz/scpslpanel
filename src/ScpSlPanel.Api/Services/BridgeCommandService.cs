using System.Collections.Concurrent;
using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed class BridgeCommandService(JsonStore store)
{
    private sealed record Pending(BridgeCommand Command, TaskCompletionSource<BridgeCommandResult> Completion);
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Pending>> _pending = new();
    private readonly SemaphoreSlim _eventGate = new(1, 1);

    public async Task<BridgeCommandResult> ExecuteAsync(
        Guid serverId, string type, string? playerId = null, string? reason = null,
        int? durationSeconds = null, string? message = null, CancellationToken cancellationToken = default)
    {
        var command = new BridgeCommand(Guid.NewGuid(), type, playerId, reason, durationSeconds,
            message, DateTimeOffset.UtcNow);
        var completion = new TaskCompletionSource<BridgeCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.GetOrAdd(serverId, _ => new())[command.Id] = new(command, completion);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "The game-server bridge did not confirm the command within 12 seconds.");
        }
        finally
        {
            if (_pending.TryGetValue(serverId, out var queue)) queue.TryRemove(command.Id, out _);
        }
    }

    public IReadOnlyList<BridgeCommand> PendingCommands(Guid serverId) =>
        _pending.TryGetValue(serverId, out var queue)
            ? queue.Values.Select(x => x.Command).OrderBy(x => x.CreatedAt).ToList() : [];

    public bool Complete(Guid serverId, Guid commandId, BridgeCommandResult result) =>
        _pending.TryGetValue(serverId, out var queue)
        && queue.TryGetValue(commandId, out var pending)
        && pending.Completion.TrySetResult(result);

    public async Task RecordEventAsync(Guid serverId, BridgeEventRequest value)
    {
        await _eventGate.WaitAsync();
        try
        {
            var activities = await store.ReadAsync<ServerActivityEntry>("server-activity");
            activities.Add(new(Guid.NewGuid(), serverId, value.At, value.Type, value.PlayerId,
                value.UserId, value.DisplayName, value.Detail ?? ""));
            if (activities.Count > 10000)
                activities = activities.OrderByDescending(x => x.At).Take(10000).ToList();
            await store.WriteAsync("server-activity", activities);

            var rounds = await store.ReadAsync<RoundHistoryEntry>("round-history");
            if (value.Type == "round-start")
                rounds.Add(new(Guid.NewGuid(), serverId, value.At, null, null, null));
            else if (value.Type == "round-end")
            {
                var index = rounds.FindLastIndex(x => x.ServerId == serverId && x.EndedAt is null);
                if (index >= 0)
                    rounds[index] = rounds[index] with
                    {
                        EndedAt = value.At,
                        LeadingTeam = value.Detail,
                        DurationSeconds = Math.Max(0, (long)(value.At - rounds[index].StartedAt).TotalSeconds)
                    };
            }
            await store.WriteAsync("round-history", rounds);
        }
        finally { _eventGate.Release(); }
    }

    public async Task<IReadOnlyList<ServerActivityEntry>> ActivityAsync(Guid serverId, int take) =>
        (await store.ReadAsync<ServerActivityEntry>("server-activity"))
        .Where(x => x.ServerId == serverId).OrderByDescending(x => x.At)
        .Take(Math.Clamp(take, 1, 1000)).ToList();

    public async Task<IReadOnlyList<RoundHistoryEntry>> RoundsAsync(Guid serverId, int take) =>
        (await store.ReadAsync<RoundHistoryEntry>("round-history"))
        .Where(x => x.ServerId == serverId).OrderByDescending(x => x.StartedAt)
        .Take(Math.Clamp(take, 1, 500)).ToList();
}
