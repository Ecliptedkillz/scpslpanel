using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed class AuditService(JsonStore store)
{
    public async Task AddAsync(string actor, string action, string target, string detail)
    {
        var entries = await store.ReadAsync<AuditEntry>("audit");
        entries.Insert(0, new(Guid.NewGuid(), DateTimeOffset.UtcNow, actor, action, target, detail));
        if (entries.Count > 2000) entries.RemoveRange(2000, entries.Count - 2000);
        await store.WriteAsync("audit", entries);
    }

    public async Task<IReadOnlyList<AuditEntry>> RecentAsync(int count = 50) =>
        (await store.ReadAsync<AuditEntry>("audit")).Take(Math.Clamp(count, 1, 250)).ToList();
}
