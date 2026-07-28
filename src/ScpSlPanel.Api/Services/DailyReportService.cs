namespace ScpSlPanel.Api.Services;

public sealed class DailyReportService(
    NotificationService notifications, ServerManager servers, ILogger<DailyReportService> logger)
    : BackgroundService
{
    private DateOnly? _lastSent;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await notifications.GetAsync();
                var now = DateTimeOffset.UtcNow;
                var today = DateOnly.FromDateTime(now.UtcDateTime);
                if (settings.DiscordDailyReportEnabled
                    && now.Hour == Math.Clamp(settings.DiscordDailyReportHourUtc, 0, 23)
                    && _lastSent != today)
                {
                    var snapshots = await servers.SnapshotsAsync();
                    var online = snapshots.Count(x => x.State == Domain.ServerState.Online);
                    var players = snapshots.Sum(x => x.Players);
                    var memory = snapshots.Sum(x => x.MemoryBytes) / 1024 / 1024;
                    var detail = string.Join('\n', snapshots.Select(x =>
                        $"**{x.Name}** — {x.State}, {x.Players}/{x.MaxPlayers} players, {x.MemoryBytes / 1024 / 1024} MB"));
                    await notifications.SendAsync("Daily server report",
                        $"{online}/{snapshots.Count} servers online • {players} players • {memory} MB total\n\n{detail}");
                    _lastSent = today;
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "Daily Discord report failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
