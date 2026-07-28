using System.Collections.Concurrent;
using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed class OperationTracker(JsonStore store)
{
    private readonly ConcurrentDictionary<Guid, OperationEntry> _active = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<OperationEntry> StartAsync(
        Guid? serverId, string type, string target, string actor, string message = "Queued")
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new OperationEntry(Guid.NewGuid(), serverId, type, target, actor, "queued",
            message, now, now);
        _active[entry.Id] = entry;
        await SaveAsync(entry);
        return entry;
    }

    public async Task UpdateAsync(Guid id, string status, string message)
    {
        if (!_active.TryGetValue(id, out var entry)) return;
        entry = entry with { Status = status, Message = message, UpdatedAt = DateTimeOffset.UtcNow };
        _active[id] = entry;
        await SaveAsync(entry);
        if (status is "completed" or "failed") _active.TryRemove(id, out _);
    }

    public async Task<IReadOnlyList<OperationEntry>> ListAsync(int take = 100) =>
        (await store.ReadAsync<OperationEntry>("operations")).OrderByDescending(x => x.UpdatedAt)
        .Take(Math.Clamp(take, 1, 500)).ToArray();

    private async Task SaveAsync(OperationEntry entry)
    {
        await _gate.WaitAsync();
        try
        {
            var values = await store.ReadAsync<OperationEntry>("operations");
            var index = values.FindIndex(x => x.Id == entry.Id);
            if (index < 0) values.Add(entry); else values[index] = entry;
            await store.WriteAsync("operations", values.OrderByDescending(x => x.UpdatedAt).Take(1000));
        }
        finally { _gate.Release(); }
    }
}
