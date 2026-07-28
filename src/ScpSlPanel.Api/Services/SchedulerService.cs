using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed class SchedulerService(
    JsonStore store, ServerManager servers, AuditService audit, ILogger<SchedulerService> logger) : BackgroundService
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
                            case "restart": await servers.RestartAsync(item.ServerId, "scheduler"); break;
                            default: await servers.CommandAsync(item.ServerId, item.Action, "scheduler"); break;
                        }
                        schedules[index] = item with { LastRunAt = now };
                        changed = true;
                        await audit.AddAsync("scheduler", "schedule.run", item.Name, item.Action);
                    }
                    catch (Exception ex) { logger.LogError(ex, "Schedule {Schedule} failed", item.Name); }
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
