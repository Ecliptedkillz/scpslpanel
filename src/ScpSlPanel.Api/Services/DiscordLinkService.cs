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

    public async Task<BridgeGameRoleAssignment> ResolveGameRoleAsync(
        ServerDefinition server, string userId)
    {
        var steamId = userId.Split('@', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(steamId)) return new(false);
        var links = ReadLinks(server);
        if (!links.TryGetValue(steamId, out var discordId)) return new(false);
        var settings = await settingsService.GetAsync();
        var grants = settings.DiscordGameRoleGrants?
            .Where(grant => grant.Enabled && grant.ServerId == server.Id
                && !string.IsNullOrWhiteSpace(grant.RoleId)
                && !string.IsNullOrWhiteSpace(grant.GroupName))
            .OrderByDescending(grant => grant.Priority).ToArray() ?? [];
        if (grants.Length == 0 || string.IsNullOrWhiteSpace(settings.DiscordBotToken)
            || string.IsNullOrWhiteSpace(settings.DiscordGuildId)) return new(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://discord.com/api/v10/guilds/{settings.DiscordGuildId}/members/{discordId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.DiscordBotToken);
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Discord game-role lookup for {DiscordId} returned {Status}",
                    discordId, (int)response.StatusCode);
                return new(false);
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var roleIds = document.RootElement.GetProperty("roles").EnumerateArray()
                .Select(role => role.GetString()).Where(role => role is not null).ToHashSet();
            var match = grants.FirstOrDefault(grant => roleIds.Contains(grant.RoleId));
            if (match is null) return new(false);
            var allServerGrants = settings.DiscordGameRoleGrants?
                .Where(grant => grant.Enabled && grant.ServerId == server.Id).ToArray() ?? [];
            var permissions = new HashSet<string>(match.Permissions ?? [], StringComparer.OrdinalIgnoreCase);
            var pluginPermissions = new HashSet<string>(
                match.PluginPermissions ?? [], StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>(match.InheritedGroups ?? []);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.TryDequeue(out var inheritedName) && visited.Add(inheritedName))
            {
                var inherited = allServerGrants.FirstOrDefault(grant =>
                    grant.GroupName.Equals(inheritedName, StringComparison.OrdinalIgnoreCase));
                if (inherited is null) continue;
                permissions.UnionWith(inherited.Permissions ?? []);
                pluginPermissions.UnionWith(inherited.PluginPermissions ?? []);
                foreach (var parent in inherited.InheritedGroups ?? []) pending.Enqueue(parent);
            }
            return new(true, match.GroupName.Trim(), discordId, match.RoleId,
                permissions.ToArray(), match.BadgeText, match.BadgeColor, match.Hidden,
                match.Cover, match.ReservedSlot, match.KickPower, match.RequiredKickPower,
                pluginPermissions.ToArray());
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Discord game-role synchronization failed for {UserId}", userId);
            return new(false);
        }
    }

    public async Task<BridgeCustomBadge> ResolveCustomBadgeAsync(
        ServerDefinition server, string userId)
    {
        var steamId = userId.Split('@', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(steamId)) return new(false);
        var settings = await settingsService.GetAsync();
        var userBadge = settings.CustomUserBadges?.LastOrDefault(badge =>
            badge.ServerId == server.Id
            && badge.SteamId.Equals(steamId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(badge.BadgeText));
        if (userBadge is not null)
            return new(true, userBadge.BadgeText.Trim(),
                string.IsNullOrWhiteSpace(userBadge.BadgeColor) ? "silver" : userBadge.BadgeColor.Trim());

        var badges = settings.CustomRoleBadges?
            .Where(badge => badge.Enabled && badge.ServerId == server.Id
                && !string.IsNullOrWhiteSpace(badge.RoleId)
                && !string.IsNullOrWhiteSpace(badge.BadgeText))
            .OrderByDescending(badge => badge.Priority).ToArray() ?? [];
        var links = ReadLinks(server);
        if (badges.Length == 0 || !links.TryGetValue(steamId, out var discordId)
            || string.IsNullOrWhiteSpace(settings.DiscordBotToken)
            || string.IsNullOrWhiteSpace(settings.DiscordGuildId)) return new(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://discord.com/api/v10/guilds/{settings.DiscordGuildId}/members/{discordId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.DiscordBotToken);
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var roleIds = document.RootElement.GetProperty("roles").EnumerateArray()
                .Select(role => role.GetString()).Where(role => role is not null).ToHashSet();
            var match = badges.FirstOrDefault(badge => roleIds.Contains(badge.RoleId));
            return match is null ? new(false) : new(true, match.BadgeText.Trim(),
                string.IsNullOrWhiteSpace(match.BadgeColor) ? "silver" : match.BadgeColor.Trim());
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Discord custom role badge lookup failed for {UserId}", userId);
            return new(false);
        }
    }

    public async Task<BridgeTagOptions> ResolveTagOptionsAsync(ServerDefinition server, string userId)
    {
        var steamId = userId.Split('@', 2)[0].Trim();
        var settings = await settingsService.GetAsync();
        var options = new List<BridgeTagOption>();
        foreach (var badge in settings.CustomUserBadges?.Where(badge =>
                     badge.ServerId == server.Id
                     && badge.SteamId.Equals(steamId, StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(badge.BadgeText)) ?? [])
            options.Add(new($"user:{badge.BadgeText.Trim()}:{badge.BadgeColor}", "custom user", badge.BadgeText.Trim(),
                string.IsNullOrWhiteSpace(badge.BadgeColor) ? "silver" : badge.BadgeColor.Trim()));

        var links = ReadLinks(server);
        if (!links.TryGetValue(steamId, out var discordId)
            || string.IsNullOrWhiteSpace(settings.DiscordBotToken)
            || string.IsNullOrWhiteSpace(settings.DiscordGuildId))
            return new(options);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://discord.com/api/v10/guilds/{settings.DiscordGuildId}/members/{discordId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.DiscordBotToken);
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new(options);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var roleIds = document.RootElement.GetProperty("roles").EnumerateArray()
                .Select(role => role.GetString()).Where(role => role is not null).ToHashSet();
            foreach (var badge in settings.CustomRoleBadges?
                         .Where(badge => badge.Enabled && badge.ServerId == server.Id
                             && roleIds.Contains(badge.RoleId)
                             && !string.IsNullOrWhiteSpace(badge.BadgeText))
                         .OrderByDescending(badge => badge.Priority).ToArray() ?? [])
                options.Add(new($"role:{badge.RoleId}", "Discord role", badge.BadgeText.Trim(),
                    string.IsNullOrWhiteSpace(badge.BadgeColor) ? "silver" : badge.BadgeColor.Trim()));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Discord tag option lookup failed for {UserId}", userId);
        }
        return new(options.GroupBy(option => $"{option.BadgeText}\0{option.BadgeColor}",
            StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray());
    }

    public async Task<IReadOnlyList<DiscordGuildRole>> ListGuildRolesAsync()
    {
        var settings = await settingsService.GetAsync();
        if (string.IsNullOrWhiteSpace(settings.DiscordBotToken)
            || string.IsNullOrWhiteSpace(settings.DiscordGuildId)) return [];
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://discord.com/api/v10/guilds/{settings.DiscordGuildId}/roles");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.DiscordBotToken);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Discord roles returned {(int)response.StatusCode}: {body}");
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray()
            .Select(role => new DiscordGuildRole(
                role.GetProperty("id").GetString() ?? "",
                role.GetProperty("name").GetString() ?? "Unnamed role",
                role.GetProperty("position").GetInt32(),
                role.TryGetProperty("color", out var color) ? color.GetUInt32() : 0))
            .Where(role => role.Name != "@everyone")
            .OrderByDescending(role => role.Position).ToArray();
    }

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
        var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localPath = LinkPath(server);
        ReadLinkFile(localPath, links, overwrite: true);

        // Discord identities belong to the player, not one game instance. Fall back to
        // links created on another server so permissions and badges work fleet-wide.
        var configsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCP Secret Laboratory", "LabAPI", "configs");
        if (Directory.Exists(configsPath))
            foreach (var path in Directory.EnumerateFiles(configsPath, "DiscordLinks.csv",
                         SearchOption.AllDirectories))
                if (!path.Equals(localPath, StringComparison.OrdinalIgnoreCase))
                    ReadLinkFile(path, links, overwrite: false);
        return links;
    }

    private void ReadLinkFile(string path, IDictionary<string, string> links, bool overwrite)
    {
        if (!File.Exists(path)) return;
        try {
            foreach (var line in File.ReadLines(path)) {
                var fields = line.Trim().Split(',', 3, StringSplitOptions.TrimEntries);
                if (fields.Length < 2 || fields[0].Length == 0 || fields[1].Length == 0) continue;
                if (overwrite) links[fields[0]] = fields[1];
                else links.TryAdd(fields[0], fields[1]);
            }
        } catch (Exception ex) { logger.LogWarning(ex, "Unable to read Discord link file {Path}", path); }
    }

    private static string LinkPath(ServerDefinition server) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCP Secret Laboratory", "LabAPI", "configs", server.QueryPort.ToString(),
        "PlayhousePlugin", "DiscordLinks.csv");
}
