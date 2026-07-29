using Discord.WebSocket;
using ScpSlPanel.Api.Domain;

namespace ScpSlPanel.Api.Services;

public sealed class DiscordDonorSyncService(
    NotificationService settingsService, ServerManager servers,
    ILogger<DiscordDonorSyncService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<DonorSyncResult>> SyncAsync(
        SocketGuild guild, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await settingsService.GetAsync();
            var definitions = await servers.DefinitionsAsync();
            var results = new List<DonorSyncResult>();
            foreach (var server in definitions)
            {
                var grants = settings.DiscordDonorRoleGrants?
                    .Where(x => x.Enabled && x.ServerId == server.Id
                        && !string.IsNullOrWhiteSpace(x.RoleId) && x.Tier > 0)
                    .OrderByDescending(x => x.Priority).ThenByDescending(x => x.Tier).ToArray() ?? [];
                if (grants.Length == 0) continue;
                results.Add(SyncServer(server, grants, guild));
            }
            return results;
        }
        finally { _gate.Release(); }
    }

    private DonorSyncResult SyncServer(
        ServerDefinition server, IReadOnlyList<DiscordDonorRoleGrant> grants, SocketGuild guild)
    {
        var links = ReadLinks(server);
        var path = DonatorPath(server);
        var existing = ReadDonators(path);
        var output = new Dictionary<string, DonatorRow>(existing, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var updated = 0;
        var removed = 0;

        foreach (var (steamId, discordId) in links)
        {
            var member = ulong.TryParse(discordId, out var id) ? guild.GetUser(id) : null;
            var match = member is null ? null : grants.FirstOrDefault(
                grant => ulong.TryParse(grant.RoleId, out var roleId)
                    && member.Roles.Any(role => role.Id == roleId));
            if (match is null)
            {
                if (output.Remove(steamId)) removed++;
                continue;
            }

            var row = new DonatorRow(match.Tier, member!.PremiumSince is not null,
                existing.TryGetValue(steamId, out var old) ? old.PetIndex : 0);
            if (!existing.TryGetValue(steamId, out var previous)) added++;
            else if (previous != row) updated++;
            output[steamId] = row;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllLines(temporary, output.OrderBy(x => x.Key).Select(x =>
            $"{x.Key},{x.Value.Tier},{x.Value.Booster.ToString().ToLowerInvariant()},{x.Value.PetIndex}"));
        File.Move(temporary, path, true);
        logger.LogInformation(
            "Synchronized {Donors} Discord donors to {Path}: {Added} added, {Updated} updated, {Removed} removed",
            output.Count, path, added, updated, removed);
        return new(server.Id, server.Name, output.Count, added, updated, removed, path);
    }

    private static Dictionary<string, string> ReadLinks(ServerDefinition server)
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(ConfigRoot(server), "PlayhousePlugin", "DiscordLinks.csv");
        if (!File.Exists(path)) return output;
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length >= 2 && ulong.TryParse(fields[0], out _) && ulong.TryParse(fields[1], out _))
                output[fields[0]] = fields[1];
        }
        return output;
    }

    private static Dictionary<string, DonatorRow> ReadDonators(string path)
    {
        var output = new Dictionary<string, DonatorRow>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return output;
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length >= 4 && ulong.TryParse(fields[0], out _)
                && int.TryParse(fields[1], out var tier)
                && bool.TryParse(fields[2], out var booster)
                && int.TryParse(fields[3], out var pet))
                output[fields[0]] = new(tier, booster, pet);
        }
        return output;
    }

    private static string DonatorPath(ServerDefinition server) =>
        Path.Combine(ConfigRoot(server), "PlayhousePlugin", "Donators.csv");

    private static string ConfigRoot(ServerDefinition server) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCP Secret Laboratory", "LabAPI", "configs", server.QueryPort.ToString());

    private sealed record DonatorRow(int Tier, bool Booster, int PetIndex);
}
