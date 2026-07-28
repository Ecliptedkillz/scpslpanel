using System.Collections.Concurrent;
using ScpSlPanel.Api.Domain;

namespace ScpSlPanel.Api.Services;

public sealed class RestartCoordinator(
    ServerManager servers, OperationsDataService operations, NotificationService notifications,
    AuditService audit, ILogger<RestartCoordinator> logger)
{
    private sealed record Job(RestartCountdownStatus Status, CancellationTokenSource Cancellation);
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();

    public RestartCountdownStatus? Get(Guid serverId) =>
        _jobs.TryGetValue(serverId, out var job) ? job.Status : null;

    public RestartCountdownStatus Schedule(Guid serverId, int seconds, string? message, string actor)
    {
        seconds = Math.Clamp(seconds, 10, 86400);
        if (_jobs.ContainsKey(serverId)) throw new InvalidOperationException("A restart countdown is already active.");
        var status = new RestartCountdownStatus(serverId, DateTimeOffset.UtcNow.AddSeconds(seconds),
            string.IsNullOrWhiteSpace(message) ? "Scheduled server restart" : message.Trim(), actor);
        var cancellation = new CancellationTokenSource();
        if (!_jobs.TryAdd(serverId, new(status, cancellation)))
            throw new InvalidOperationException("A restart countdown is already active.");
        _ = RunAsync(status, cancellation.Token);
        return status;
    }

    public async Task<bool> CancelAsync(Guid serverId, string actor)
    {
        if (!_jobs.TryRemove(serverId, out var job)) return false;
        job.Cancellation.Cancel();
        job.Cancellation.Dispose();
        await audit.AddAsync(actor, "restart.cancel", serverId.ToString(), job.Status.Message);
        try { await servers.CommandAsync(serverId, "bc 8 Scheduled restart cancelled.", actor); } catch { }
        return true;
    }

    private async Task RunAsync(RestartCountdownStatus status, CancellationToken cancellationToken)
    {
        try
        {
            var warnings = new[] { 3600, 1800, 900, 600, 300, 120, 60, 30, 10 }
                .Where(value => value < (status.DueAt - DateTimeOffset.UtcNow).TotalSeconds)
                .Append(0).OrderByDescending(x => x);
            foreach (var remaining in warnings)
            {
                var delay = status.DueAt.AddSeconds(-remaining) - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
                if (remaining > 0)
                {
                    var readable = remaining >= 60 ? $"{remaining / 60} minute(s)" : $"{remaining} seconds";
                    try { await servers.CommandAsync(status.ServerId, $"bc 10 {status.Message} in {readable}.", "restart-manager"); }
                    catch (Exception ex) { logger.LogDebug(ex, "Restart warning command failed"); }
                }
            }
            await operations.AddIncidentAsync(status.ServerId, "scheduled-restart", status.Message);
            var settings = await notifications.GetAsync();
            if (settings.NotifyRestart)
                await notifications.SendAsync("Scheduled restart", status.Message, "warning");
            await servers.RestartAsync(status.ServerId, status.Actor);
            await audit.AddAsync(status.Actor, "restart.complete", status.ServerId.ToString(), status.Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restart countdown failed for {ServerId}", status.ServerId);
            await operations.AddIncidentAsync(status.ServerId, "restart-failed", ex.Message);
        }
        finally
        {
            if (_jobs.TryRemove(status.ServerId, out var job)) job.Cancellation.Dispose();
        }
    }
}
