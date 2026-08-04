using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed record HealthCheckResult(string Key, string Name, string Status, string Detail, string? Action = null);
public sealed record DeploymentHealthReport(string Status, DateTimeOffset CheckedAt, IReadOnlyList<HealthCheckResult> Checks);

public sealed class DeploymentHealthService(
    JsonStore store, IConfiguration configuration, ServerManager servers, BridgeStateService bridge,
    DiscordBotService discord, OperationsDataService operations)
{
    public async Task<DeploymentHealthReport> CheckAsync()
    {
        var checks = new List<HealthCheckResult>();
        var dataPath = store.StoragePath();
        try
        {
            Directory.CreateDirectory(dataPath);
            var probe = Path.Combine(dataPath, $".health-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "ok");
            File.Delete(probe);
            checks.Add(new("storage", "Panel storage", "healthy", $"Writable at {dataPath}"));
        }
        catch (Exception exception)
        {
            checks.Add(new("storage", "Panel storage", "critical", exception.Message, "Check folder permissions and free disk space."));
        }

        var root = Path.GetPathRoot(dataPath) ?? dataPath;
        try
        {
            var drive = new DriveInfo(root);
            var freePercent = drive.TotalSize == 0 ? 0 : drive.AvailableFreeSpace * 100d / drive.TotalSize;
            checks.Add(new("disk", "Disk space", freePercent < 5 ? "critical" : freePercent < 15 ? "warning" : "healthy",
                $"{freePercent:F1}% free ({drive.AvailableFreeSpace / 1_073_741_824d:F1} GB)",
                freePercent < 15 ? "Free disk space or move backups off-machine." : null));
        }
        catch (Exception exception) { checks.Add(new("disk", "Disk space", "warning", exception.Message)); }

        var oauthConfigured = !string.IsNullOrWhiteSpace(configuration["Panel:DiscordOAuth:ClientId"])
            && !string.IsNullOrWhiteSpace(configuration["Panel:DiscordOAuth:ClientSecret"]);
        checks.Add(new("discord-oauth", "Discord staff login", oauthConfigured ? "healthy" : "warning",
            oauthConfigured ? "OAuth client credentials are configured." : "Discord login is disabled.",
            oauthConfigured ? null : "Set the Discord OAuth values in .env."));

        var bot = discord.Status;
        checks.Add(new("discord-bot", "Discord bot", !bot.Enabled ? "warning" : bot.Connected ? "healthy" : "critical",
            !bot.Enabled ? "Bot integration is disabled." : bot.Connected ? $"Connected as {bot.BotName}." : bot.Error ?? "Bot is offline.",
            bot.Enabled && !bot.Connected ? "Review Discord diagnostics and bot permissions." : null));

        var definitions = await servers.DefinitionsAsync();
        foreach (var server in definitions)
        {
            var state = bridge.Get(server.Id);
            checks.Add(new($"bridge:{server.Id}", $"{server.Name} bridge", state.Connected ? "healthy" : "warning",
                state.Connected ? $"Connected; last heartbeat {state.LastSeenAt:O}." : "No recent bridge heartbeat.",
                state.Connected ? null : "Verify the LabAPI bridge URL and token."));
            var latest = (await operations.BackupsAsync(server.Id)).OrderByDescending(value => value.CreatedAt).FirstOrDefault();
            var stale = latest is null || latest.CreatedAt < DateTimeOffset.UtcNow.AddDays(-7);
            checks.Add(new($"backup:{server.Id}", $"{server.Name} backup", stale ? "warning" : "healthy",
                latest is null ? "No backup has been recorded." : $"Latest backup: {latest.CreatedAt:O}.",
                stale ? "Create or schedule a current backup." : null));
        }

        if (definitions.Count == 0)
            checks.Add(new("servers", "Game servers", "warning", "No SCP:SL servers are registered.", "Complete server onboarding."));
        var overall = checks.Any(value => value.Status == "critical") ? "critical"
            : checks.Any(value => value.Status == "warning") ? "warning" : "healthy";
        return new(overall, DateTimeOffset.UtcNow, checks);
    }
}
