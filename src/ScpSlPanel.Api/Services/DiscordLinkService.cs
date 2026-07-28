using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ScpSlPanel.Api.Domain;

namespace ScpSlPanel.Api.Services;

public sealed class DiscordLinkService(NotificationService settingsService, ILogger<DiscordLinkService> logger)
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly Dictionary<string, (DateTimeOffset Expires, PlayerRecord Value)> _steamCache = [];
    private readonly Dictionary<string, (DateTimeOffset Expires, PlayerRecord Value)> _discordCache = [];
    private readonly object _cacheGate = new();

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

    public IReadOnlyList<IdentityLinkHealth> Health(ServerDefinition server)
    {
        var path = LinkPath(server);
        if (!File.Exists(path)) return [];
        var rows = new List<(int Line, string Steam, string Discord)>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Trim().Split(',', 3, StringSplitOptions.TrimEntries);
            rows.Add((lineNumber, fields.ElementAtOrDefault(0) ?? "", fields.ElementAtOrDefault(1) ?? ""));
        }
        return rows.Select(row =>
        {
            var issues = new List<string>();
            if (!ulong.TryParse(row.Steam, out _)) issues.Add("Invalid Steam ID");
            if (!ulong.TryParse(row.Discord, out _)) issues.Add("Invalid Discord ID");
            if (rows.Count(x => x.Steam == row.Steam) > 1) issues.Add("Duplicate Steam ID");
            if (rows.Count(x => x.Discord == row.Discord) > 1) issues.Add("Duplicate Discord ID");
            return new IdentityLinkHealth(server.Id, server.Name, row.Line, row.Steam, row.Discord,
                issues.Count == 0, issues.Count == 0 ? null : string.Join(", ", issues));
        }).ToArray();
    }

    private async Task<PlayerRecord> AddSteamAsync(PlayerRecord player, string steamId, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !ulong.TryParse(steamId, out _)) return player;
        lock (_cacheGate)
            if (_steamCache.TryGetValue(steamId, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
                return ApplySteam(player, cached.Value);
        try
        {
            var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(key)}&steamids={steamId}";
            using var document = JsonDocument.Parse(await _http.GetStringAsync(url));
            var profile = document.RootElement.GetProperty("response").GetProperty("players").EnumerateArray().FirstOrDefault();
            if (profile.ValueKind == JsonValueKind.Undefined) return player;
            var enriched = player with {
                SteamDisplayName = profile.GetProperty("personaname").GetString(),
                SteamAvatarUrl = profile.GetProperty("avatarfull").GetString(),
                SteamProfileUrl = profile.GetProperty("profileurl").GetString()
            };
            lock (_cacheGate) _steamCache[steamId] = (DateTimeOffset.UtcNow.AddMinutes(30), enriched);
            return enriched;
        }
        catch (Exception ex) { logger.LogDebug(ex, "Steam profile lookup failed for {SteamId}", steamId); return player; }
    }

    private async Task<PlayerRecord> AddDiscordAsync(PlayerRecord player, string discordId, PanelIntegrationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DiscordBotToken)
            || !ulong.TryParse(discordId, out var numericDiscordId)) return player;
        lock (_cacheGate)
            if (_discordCache.TryGetValue($"{settings.DiscordGuildId}:{discordId}", out var cached)
                && cached.Expires > DateTimeOffset.UtcNow)
                return ApplyDiscord(player, cached.Value);
        var enriched = player;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://discord.com/api/v10/users/{discordId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.DiscordBotToken);
            using var response = await _http.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogInformation(
                    "Discord user {DiscordId} is not a member of guild {GuildId}, or the configured guild ID is incorrect; basic account details will be shown",
                    discordId, settings.DiscordGuildId);
                lock (_cacheGate) _discordCache[$"{settings.DiscordGuildId}:{discordId}"] =
                    (DateTimeOffset.UtcNow.AddMinutes(5), enriched);
                return enriched;
            }
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                logger.LogWarning(
                    "Discord guild member lookup for {DiscordId} returned {Status}: {Body}",
                    discordId, (int)response.StatusCode, body);
                return enriched;
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var user = document.RootElement;
            var username = user.GetProperty("username").GetString();
            var avatar = user.TryGetProperty("avatar", out var avatarValue) ? avatarValue.GetString() : null;
            var discriminator = user.TryGetProperty("discriminator", out var discriminatorValue)
                ? discriminatorValue.GetString() : "0";
            var defaultAvatar = discriminator is not null && discriminator != "0"
                && int.TryParse(discriminator, out var legacyDiscriminator)
                ? legacyDiscriminator % 5 : (int)((numericDiscordId >> 22) % 6);
            enriched = player with {
                DiscordDisplayName = username,
                DiscordAvatarUrl = avatar is null
                    ? $"https://cdn.discordapp.com/embed/avatars/{defaultAvatar}.png"
                    : $"https://cdn.discordapp.com/avatars/{discordId}/{avatar}.png?size=256"
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord user lookup failed for linked user {DiscordId}", discordId);
            return enriched;
        }

        if (string.IsNullOrWhiteSpace(settings.DiscordGuildId)) return enriched;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://discord.com/api/v10/guilds/{settings.DiscordGuildId}/members/{discordId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.DiscordBotToken);
            using var response = await _http.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogInformation(
                    "Discord user {DiscordId} is not visible as a member of guild {GuildId}. "
                    + "Confirm the user and bot are both in that Discord server and the Guild ID is correct. "
                    + "Basic Discord account details will still be shown.",
                    discordId, settings.DiscordGuildId);
                lock (_cacheGate) _discordCache[$"{settings.DiscordGuildId}:{discordId}"] =
                    (DateTimeOffset.UtcNow.AddMinutes(5), enriched);
                return enriched;
            }
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                logger.LogWarning(
                    "Discord member lookup for {DiscordId} in guild {GuildId} returned {Status}: {Body}",
                    discordId, settings.DiscordGuildId, (int)response.StatusCode, body);
                return enriched;
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var member = document.RootElement;
            var roleIds = member.GetProperty("roles").EnumerateArray()
                .Select(x => x.GetString()).Where(x => x is not null).ToHashSet();
            var guildRoles = await GetGuildRolesAsync(settings);
            var completed = enriched with {
                DiscordDisplayName = member.TryGetProperty("nick", out var nick)
                    && nick.ValueKind != JsonValueKind.Null ? nick.GetString() : enriched.DiscordDisplayName,
                DiscordRoles = guildRoles.Where(x => roleIds.Contains(x.Id)).Select(x => x.Name).ToArray()
            };
            lock (_cacheGate) _discordCache[$"{settings.DiscordGuildId}:{discordId}"] =
                (DateTimeOffset.UtcNow.AddMinutes(10), completed);
            return completed;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex,
                "Discord guild details unavailable for {DiscordId}; basic account profile will still be shown",
                discordId);
            lock (_cacheGate) _discordCache[$"{settings.DiscordGuildId}:{discordId}"] =
                (DateTimeOffset.UtcNow.AddMinutes(5), enriched);
            return enriched;
        }
    }

    private static PlayerRecord ApplySteam(PlayerRecord target, PlayerRecord cached) => target with {
        SteamDisplayName = cached.SteamDisplayName, SteamAvatarUrl = cached.SteamAvatarUrl,
        SteamProfileUrl = cached.SteamProfileUrl
    };
    private static PlayerRecord ApplyDiscord(PlayerRecord target, PlayerRecord cached) => target with {
        DiscordDisplayName = cached.DiscordDisplayName, DiscordAvatarUrl = cached.DiscordAvatarUrl,
        DiscordRoles = cached.DiscordRoles
    };

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
        var path = LinkPath(server);
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

    private static string LinkPath(ServerDefinition server) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCP Secret Laboratory", "LabAPI", "configs", server.QueryPort.ToString(),
        "PlayhousePlugin", "DiscordLinks.csv");
}
