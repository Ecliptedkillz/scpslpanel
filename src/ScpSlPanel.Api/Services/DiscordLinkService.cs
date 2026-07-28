using ScpSlPanel.Api.Domain;

namespace ScpSlPanel.Api.Services;

public sealed class DiscordLinkService(ILogger<DiscordLinkService> logger)
{
    public IReadOnlyList<PlayerRecord> Enrich(ServerDefinition server, IReadOnlyList<PlayerRecord> players)
    {
        var links = ReadLinks(server);
        if (links.Count == 0) return players;
        return players.Select(player =>
        {
            var steamId = player.UserId.Split('@', 2)[0].Trim();
            return links.TryGetValue(steamId, out var discordId)
                ? player with { DiscordId = discordId }
                : player;
        }).ToList();
    }

    public PlayerRecord Enrich(ServerDefinition server, PlayerRecord player) =>
        Enrich(server, [player])[0];

    private Dictionary<string, string> ReadLinks(ServerDefinition server)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCP Secret Laboratory", "LabAPI", "configs", server.QueryPort.ToString(),
            "PlayhousePlugin", "DiscordLinks.csv");
        var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return links;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var fields = line.Trim().Split(',', 3, StringSplitOptions.TrimEntries);
                if (fields.Length >= 2 && fields[0].Length > 0 && fields[1].Length > 0)
                    links[fields[0]] = fields[1];
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read Discord link file {Path}", path);
        }
        return links;
    }
}
