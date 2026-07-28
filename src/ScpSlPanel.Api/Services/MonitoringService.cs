using ScpSlPanel.Api.Domain;

namespace ScpSlPanel.Api.Services;

public sealed class MonitoringService(
    ServerManager servers, BridgeStateService bridge, OperationsDataService operations,
    NotificationService notifications, ILogger<MonitoringService> logger) : BackgroundService
{
    private readonly Dictionary<Guid, bool> _bridgeStates = [];

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
                    if (_bridgeStates.TryGetValue(snapshot.Id, out var wasConnected) && wasConnected && !connected
                        && snapshot.State == ServerState.Online)
                    {
                        await operations.AddIncidentAsync(snapshot.Id, "bridge-offline", "LabAPI bridge heartbeat timed out.");
                        var settings = await notifications.GetAsync();
                        if (settings.NotifyBridgeOffline)
                            await notifications.SendAsync($"{snapshot.Name} bridge offline",
                                "The LabAPI bridge stopped sending heartbeats.", "warning");
                    }
                    _bridgeStates[snapshot.Id] = connected;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Monitoring sample failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
