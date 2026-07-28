using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ScpSlPanel.Api.Domain;

namespace ScpSlPanel.Api.Services;

public sealed class DiscordLinkService(NotificationService settingsService, ILogger<DiscordLinkService> logger)
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<IReadOnlyList<PlayerRecord>> EnrichAsync(
        ServerDefinition server, IReadOnlyList<PlayerRecord> players)
    {
        var links = ReadLinks(server);
        var settings = await settingsService.GetAsync();
        var output = new List<PlayerRecord>(players.Count);
        foreach (var player in players)
        {
            var steamId = player.UserId.Split('@', 2)[0].Trim();
            var enriched = links.TryGetValue(steamId, out var discordId)
                ? player with { DiscordId = discordId } : player;
            enriched = await AddSteamAsync(enriched, steamId, settings.SteamWebApiKey);
            if (!string.IsNullOrWhiteSpace(discordId))
                enriched = await AddDiscordAsync(enriched, discordId, settings);
            output.Add(enriched);
        }
        return output;
    }

    public async Task<PlayerRecord> EnrichAsync(ServerDefinition server, PlayerRecord player) =>
        (await EnrichAsync(server, [player]))[0];

    private async Task<PlayerRecord> AddSteamAsync(PlayerRecord player, string steamId, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !ulong.TryParse(steamId, out _)) return player;
        try
        {
            var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(key)}&steamids={steamId}";
            using var document = JsonDocument.Parse(await _http.GetStringAsync(url));
            var profile = document.RootElement.GetProperty("response").GetProperty("players").EnumerateArray().FirstOrDefault();
            if (profile.ValueKind == JsonValueKind.Undefined) return player;
            return player with {
                SteamDisplayName = profile.GetProperty("personaname").GetString(),
                SteamAvatarUrl = profile.GetProperty("avatarfull").GetString(),
                SteamProfileUrl = profile.GetProperty("profileurl").GetString()
            };
        }
        catch (Exception ex) { logger.LogDebug(ex, "Steam profile lookup failed for {SteamId}", steamId); return player; }
    }

    private async Task<PlayerRecord> AddDiscordAsync(PlayerRecord player, string discordId, PanelIntegrationSettings settings)
    {
        if (!settings.DiscordBotEnabled || string.IsNullOrWhiteSpace(settings.DiscordBotToken)
            || settings.DiscordGuildId == 0 || !ulong.TryParse(discordId, out _)) return player;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://discord.com/api/v10/guilds/{settings.DiscordGuildId}/members/{discordId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.DiscordBotToken);
            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            var user = root.GetProperty("user");
            var username = user.GetProperty("username").GetString();
            var avatar = user.TryGetProperty("avatar", out var avatarValue) ? avatarValue.GetString() : null;
            var roleIds = root.GetProperty("roles").EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).ToHashSet();
            var guildRoles = await GetGuildRolesAsync(settings);
            return player with {
                DiscordDisplayName = root.TryGetProperty("nick", out var nick) && nick.ValueKind != JsonValueKind.Null ? nick.GetString() : username,
                DiscordAvatarUrl = avatar is null ? null : $"https://cdn.discordapp.com/avatars/{discordId}/{avatar}.png?size=256",
                DiscordRoles = guildRoles.Where(x => roleIds.Contains(x.Id)).Select(x => x.Name).ToArray()
            };
        }
        catch (Exception ex) { logger.LogDebug(ex, "Discord profile lookup failed for {DiscordId}", discordId); return player; }
    }

    private async Task<IReadOnlyList<(string Id, string Name)>> GetGuildRolesAsync(PanelIntegrationSettings settings)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://discord.com/api/v10/guilds/{settings.DiscordGuildId}/roles");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.DiscordBotToken);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var roles = await response.Content.ReadFromJsonAsync<JsonElement>();
        return roles.EnumerateArray().Select(x => (x.GetProperty("id").GetString()!, x.GetProperty("name").GetString()!)).ToArray();
    }

    private Dictionary<string, string> ReadLinks(ServerDefinition server)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCP Secret Laboratory", "LabAPI", "configs", server.QueryPort.ToString(),
            "PlayhousePlugin", "DiscordLinks.csv");
        var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return links;
        try {
            foreach (var line in File.ReadLines(path)) {
                var fields = line.Trim().Split(',', 3, StringSplitOptions.TrimEntries);
                if (fields.Length >= 2 && fields[0].Length > 0 && fields[1].Length > 0) links[fields[0]] = fields[1];
            }
        } catch (Exception ex) { logger.LogWarning(ex, "Unable to read Discord link file {Path}", path); }
        return links;
    }
}
