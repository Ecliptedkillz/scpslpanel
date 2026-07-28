using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed class SchedulerService(
    JsonStore store, ServerManager servers, RestartCoordinator restarts,
    AuditService audit, OperationsDataService operations, NotificationService notifications,
    MaintenanceService maintenance,
    ILogger<SchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.Now;
                var schedules = await store.ReadAsync<ScheduleEntry>("schedules", stoppingToken);
                var changed = false;
                for (var index = 0; index < schedules.Count; index++)
                {
                    var item = schedules[index];
                    if (!item.Enabled || !IsDue(item.Cron, now, item.LastRunAt)) continue;
                    try
                    {
                        switch (item.Action.ToLowerInvariant())
                        {
                            case "start": await servers.StartAsync(item.ServerId, "scheduler"); break;
                            case "stop": await servers.StopAsync(item.ServerId, "scheduler"); break;
                            case "restart":
                                if (item.WarningSeconds > 0)
                                    restarts.Schedule(item.ServerId, item.WarningSeconds, item.Name, "scheduler");
                                else await servers.RestartAsync(item.ServerId, "scheduler");
                                break;
                            case "backup": await maintenance.BackupAsync(item.ServerId, "scheduler"); break;
                            case "update": await maintenance.UpdateAsync(item.ServerId, "scheduler"); break;
                            case "backup-update-restart":
                                await maintenance.UpdateAsync(item.ServerId, "scheduler");
                                await servers.StartAsync(item.ServerId, "scheduler");
                                break;
                            default: await servers.CommandAsync(item.ServerId, item.Action, "scheduler"); break;
                        }
                        schedules[index] = item with { LastRunAt = now };
                        changed = true;
                        await audit.AddAsync("scheduler", "schedule.run", item.Name, item.Action);
                    }
                    catch (Exception ex)
                    {
                        var server = await servers.FindAsync(item.ServerId);
                        var settings = await notifications.GetAsync();
                        var message = NotificationService.Format(settings.ScheduleFailureMessage,
                            ("schedule", item.Name), ("server", server?.Name ?? item.ServerId.ToString()),
                            ("error", ex.Message));
                        await operations.AddIncidentAsync(item.ServerId, "schedule-failure", message);
                        await notifications.SendAsync($"Schedule failed: {item.Name}", message, "error");
                        logger.LogError(ex, "Schedule {Schedule} failed", item.Name);
                    }
                }
                if (changed) await store.WriteAsync("schedules", schedules, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduler loop failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    // Five fields: minute hour day month weekday. Supports *, exact values, and */n.
    private static bool IsDue(string expression, DateTimeOffset now, DateTimeOffset? lastRun)
    {
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return false;
        if (lastRun is { } last && last.LocalDateTime.ToString("yyyyMMddHHmm") == now.LocalDateTime.ToString("yyyyMMddHHmm"))
            return false;
        var values = new[] { now.Minute, now.Hour, now.Day, now.Month, (int)now.DayOfWeek };
        return fields.Select((field, i) => Matches(field, values[i])).All(x => x);
    }

    private static bool Matches(string field, int value)
    {
        if (field == "*") return true;
        if (field.StartsWith("*/") && int.TryParse(field[2..], out var interval) && interval > 0)
            return value % interval == 0;
        return field.Split(',').Any(part => int.TryParse(part, out var exact) && exact == value);
    }
}
