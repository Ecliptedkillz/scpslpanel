using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed class AuditService(JsonStore store, NotificationService notifications)
{
    public async Task AddAsync(string actor, string action, string target, string detail)
    {
        var entries = await store.ReadAsync<AuditEntry>("audit");
        entries.Insert(0, new(Guid.NewGuid(), DateTimeOffset.UtcNow, actor, action, target, detail));
        if (entries.Count > 2000) entries.RemoveRange(2000, entries.Count - 2000);
        await store.WriteAsync("audit", entries);
        var settings = await notifications.GetAsync();
        if (settings.NotifyAdminActions && actor != "scheduler")
            await notifications.SendAsync($"Admin action: {action}", $"**{actor}** → **{target}**\n{detail}", category: "audit");
    }

    public async Task<IReadOnlyList<AuditEntry>> RecentAsync(int count = 50) =>
        (await store.ReadAsync<AuditEntry>("audit")).Take(Math.Clamp(count, 1, 250)).ToList();

    public async Task<IReadOnlyList<AuditEntry>> SearchAsync(
        int count, string? query, string? actor, string? action, DateTimeOffset? from, DateTimeOffset? to)
    {
        IEnumerable<AuditEntry> entries = await store.ReadAsync<AuditEntry>("audit");
        if (!string.IsNullOrWhiteSpace(query))
            entries = entries.Where(x => x.Target.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.Detail.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(actor))
            entries = entries.Where(x => x.Actor.Contains(actor, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(action))
            entries = entries.Where(x => x.Action.Contains(action, StringComparison.OrdinalIgnoreCase));
        if (from is not null) entries = entries.Where(x => x.At >= from);
        if (to is not null) entries = entries.Where(x => x.At <= to);
        return entries.Take(Math.Clamp(count, 1, 2000)).ToList();
    }
}
