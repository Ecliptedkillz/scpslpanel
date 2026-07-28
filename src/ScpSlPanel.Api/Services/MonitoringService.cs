using ScpSlPanel.Api.Domain;

namespace ScpSlPanel.Api.Services;

public sealed class MonitoringService(
    ServerManager servers, BridgeStateService bridge, OperationsDataService operations,
    NotificationService notifications, ILogger<MonitoringService> logger) : BackgroundService
{
    private readonly Dictionary<Guid, bool> _bridgeStates = [];
    private readonly Dictionary<(Guid ServerId, string Rule), DateTimeOffset> _lastAlerts = [];
    private readonly Dictionary<(Guid ServerId, string Rule), int> _consecutiveSamples = [];
    private readonly HashSet<(Guid ServerId, string Rule)> _activeAlerts = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var snapshot in await servers.SnapshotsAsync())
                {
                    var connected = bridge.Get(snapshot.Id).Connected;
                    await operations.AddMetricAsync(snapshot, connected);
                    var settings = await notifications.GetAsync();
                    if (_bridgeStates.TryGetValue(snapshot.Id, out var wasConnected) && wasConnected && !connected
                        && snapshot.State == ServerState.Online)
                    {
                        var message = NotificationService.Format(settings.BridgeOfflineMessage,
                            ("server", snapshot.Name));
                        await operations.AddIncidentAsync(snapshot.Id, "bridge-offline", message);
                        if (settings.NotifyBridgeOffline)
                            await notifications.SendAsync($"{snapshot.Name}: bridge offline", message, "warning");
                    }
                    else if (_bridgeStates.TryGetValue(snapshot.Id, out wasConnected) && !wasConnected && connected)
                        await notifications.SendAsync($"{snapshot.Name}: bridge recovered",
                            "The LabAPI bridge is connected again and live player telemetry has resumed.");
                    _bridgeStates[snapshot.Id] = connected;

                    await CheckThresholdAsync(snapshot, settings, "high-cpu",
                        settings.NotifyHighCpu && snapshot.State == ServerState.Online
                            && snapshot.CpuPercent >= settings.HighCpuPercent,
                        $"{snapshot.Name}: high CPU usage",
                        NotificationService.Format(settings.HighCpuMessage, ("server", snapshot.Name),
                            ("cpu", Math.Round(snapshot.CpuPercent, 1)), ("threshold", settings.HighCpuPercent)));
                    var memoryMb = snapshot.MemoryBytes / 1024d / 1024d;
                    await CheckThresholdAsync(snapshot, settings, "high-memory",
                        settings.NotifyHighMemory && snapshot.State == ServerState.Online
                            && memoryMb >= settings.HighMemoryMb,
                        $"{snapshot.Name}: high memory usage",
                        NotificationService.Format(settings.HighMemoryMessage, ("server", snapshot.Name),
                            ("memoryMb", Math.Round(memoryMb)), ("thresholdMb", settings.HighMemoryMb)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Monitoring sample failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task CheckThresholdAsync(
        ServerSnapshot snapshot, PanelIntegrationSettings settings, string rule,
        bool exceeded, string title, string message)
    {
        var key = (snapshot.Id, rule);
        _consecutiveSamples[key] = exceeded ? _consecutiveSamples.GetValueOrDefault(key) + 1 : 0;
        if (!exceeded && _activeAlerts.Remove(key))
        {
            await notifications.SendAsync($"{snapshot.Name}: {rule} recovered",
                $"The {rule.Replace('-', ' ')} condition has returned below its configured threshold.");
            return;
        }
        if (_consecutiveSamples[key] < 2) return;
        var cooldown = TimeSpan.FromMinutes(Math.Clamp(settings.AlertCooldownMinutes, 1, 1440));
        if (_lastAlerts.TryGetValue(key, out var last) && DateTimeOffset.UtcNow - last < cooldown) return;
        _lastAlerts[key] = DateTimeOffset.UtcNow;
        _activeAlerts.Add(key);
        await operations.AddIncidentAsync(snapshot.Id, rule, message);
        await notifications.SendAsync(title, message, "warning");
    }
}
