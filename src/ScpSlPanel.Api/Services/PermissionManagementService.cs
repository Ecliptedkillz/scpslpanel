using System.Text.RegularExpressions;
using ScpSlPanel.Api.Domain;

namespace ScpSlPanel.Api.Services;

public sealed class PermissionManagementService(
    NotificationService notifications, ServerManager servers, BridgeStateService bridge,
    DiscordLinkService discordLinks)
{
    public static readonly string[] NativePermissions =
    [
        "KickingAndShortTermBanning", "BanningUpToDay", "LongTermBanning",
        "ForceclassSelf", "ForceclassToSpectator", "ForceclassWithoutRestrictions",
        "GivingItems", "WarheadEvents", "RespawnEvents", "RoundEvents", "SetGroup",
        "GameplayData", "Overwatch", "FacilityManagement", "PlayersManagement",
        "PermissionsManagement", "ServerConsoleCommands", "ViewHiddenBadges", "ServerConfigs",
        "Broadcasting", "PlayerSensitiveDataAccess", "Noclip", "AFKImmunity", "AdminChat",
        "ViewHiddenGlobalBadges", "Announcer", "Effects", "FriendlyFireDetectorImmunity",
        "FriendlyFireDetectorTempDisable", "ServerLogLiveFeed", "ExecuteAs", "Vanish"
    ];

    public async Task<PermissionHealth> HealthAsync()
    {
        var settings = await notifications.GetAsync();
        var definitions = await servers.DefinitionsAsync();
        var grants = settings.DiscordGameRoleGrants?.ToArray() ?? [];
        var issues = Validate(grants, definitions).ToList();
        try
        {
            var guildRoleIds = (await discordLinks.ListGuildRolesAsync())
                .Select(role => role.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var grant in grants.Where(grant => !string.IsNullOrWhiteSpace(grant.RoleId)
                && !guildRoleIds.Contains(grant.RoleId)))
                issues.Add(new("error", "discord-role.deleted",
                    $"Discord role {grant.RoleId} no longer exists in the configured guild.",
                    grant.ServerId, grant.GroupName));
        }
        catch (Exception exception)
        {
            issues.Add(new("warning", "discord-role.unavailable",
                $"Discord roles could not be verified: {exception.Message}"));
        }
        var runtime = new List<PermissionRoleRuntime>();
        foreach (var grant in grants)
        {
            var server = definitions.FirstOrDefault(value => value.Id == grant.ServerId);
            var status = bridge.Get(grant.ServerId);
            var online = 0;
            if (server is not null && status.Connected)
                foreach (var player in status.Players.Where(player => !string.IsNullOrWhiteSpace(player.UserId)))
                {
                    var assignment = await discordLinks.ResolveGameRoleAsync(server, player.UserId);
                    if (assignment.Assigned && assignment.GroupName?.Equals(
                        grant.GroupName, StringComparison.OrdinalIgnoreCase) == true) online++;
                }
            runtime.Add(new(grant.ServerId, server?.Name ?? "Unknown server", grant.GroupName,
                grant.RoleId, grant.Priority, grant.Enabled, grant.Permissions?.Count ?? 0,
                grant.PluginPermissions?.Count ?? 0, online, status.Connected, status.LastSeenAt));
        }
        return new(issues, runtime, NativePermissions,
            grants.SelectMany(value => value.PluginPermissions ?? []).Append("scpcontrol.permissions")
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray());
    }

    public async Task<PlayerPermissionDiagnostic?> DiagnoseAsync(Guid serverId, string userId)
    {
        var server = await servers.FindAsync(serverId);
        if (server is null) return null;
        var status = bridge.Get(serverId);
        var player = status.Players.FirstOrDefault(value =>
            value.UserId.Equals(userId, StringComparison.OrdinalIgnoreCase)
            || value.Id.Equals(userId, StringComparison.OrdinalIgnoreCase));
        var resolvedUserId = player?.UserId ?? userId;
        var assignment = await discordLinks.ResolveGameRoleAsync(server, resolvedUserId);
        var settings = await notifications.GetAsync();
        var grant = settings.DiscordGameRoleGrants?.FirstOrDefault(value =>
            value.ServerId == serverId && value.GroupName.Equals(
                assignment.GroupName, StringComparison.OrdinalIgnoreCase));
        var issues = new List<PermissionIssue>();
        if (!status.Connected) issues.Add(new("warning", "bridge.offline",
            "The bridge is offline; runtime assignment cannot be confirmed.", serverId));
        if (!assignment.Assigned) issues.Add(new("error", "role.no-match",
            "No enabled Discord role mapping matched this player.", serverId));
        return new(serverId, server.Name, resolvedUserId, player?.Nickname, player is not null,
            player?.Role, assignment, grant?.InheritedGroups ?? [], issues);
    }

    public async Task<NativeRaComparison?> CompareNativeAsync(Guid serverId)
    {
        var server = await servers.FindAsync(serverId);
        if (server is null) return null;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "SCP Secret Laboratory", "config",
            server.QueryPort.ToString());
        var candidates = new[]
        {
            Path.Combine(directory, "config_remoteadmin.txt"),
            Path.Combine(directory, "remoteadmin.txt"),
            Path.Combine(directory, "config_remoteadmin.yml")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        var panelGroups = (await notifications.GetAsync()).DiscordGameRoleGrants?
            .Where(value => value.ServerId == serverId)
            .Select(value => value.GroupName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        if (path is null) return new(serverId, server.Name, false, null, [], panelGroups, [],
            [new("warning", "native.not-found", $"No native RA configuration was found in {directory}.", serverId)]);
        var text = await File.ReadAllTextAsync(path);
        var groups = Regex.Matches(text, @"(?im)^\s*([a-z0-9_-]+)_badge\s*:")
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var members = Regex.Matches(text, @"(?im)^\s*-\s*([^:\r\n]+)\s*:\s*([a-z0-9_-]+)")
            .Select(match => $"{match.Groups[1].Value.Trim()} → {match.Groups[2].Value.Trim()}").ToArray();
        var issues = panelGroups.Where(group => groups.Contains(group, StringComparer.OrdinalIgnoreCase))
            .Select(group => new PermissionIssue("warning", "native.group-conflict",
                $"Panel role '{group}' also exists in native RA configuration.", serverId, group)).ToArray();
        return new(serverId, server.Name, true, path, groups, panelGroups, members, issues);
    }

    public IReadOnlyList<PermissionIssue> Validate(
        IReadOnlyList<DiscordGameRoleGrant> grants, IReadOnlyList<ServerDefinition> definitions)
    {
        var issues = new List<PermissionIssue>();
        foreach (var grant in grants)
        {
            if (!definitions.Any(value => value.Id == grant.ServerId))
                issues.Add(new("error", "server.missing", "The assigned server no longer exists.",
                    grant.ServerId, grant.GroupName));
            if (string.IsNullOrWhiteSpace(grant.RoleId))
                issues.Add(new("error", "discord-role.missing", "No Discord role is selected.",
                    grant.ServerId, grant.GroupName));
            if (string.IsNullOrWhiteSpace(grant.GroupName))
                issues.Add(new("error", "group.empty", "Rank name is required.", grant.ServerId));
            foreach (var permission in grant.Permissions ?? [])
                if (!NativePermissions.Contains(permission, StringComparer.OrdinalIgnoreCase))
                    issues.Add(new("warning", "permission.unknown",
                        $"Unknown native permission '{permission}'.", grant.ServerId, grant.GroupName));
            if (grant.RequiredKickPower > grant.KickPower && grant.KickPower > 0)
                issues.Add(new("info", "kick-power.restricted",
                    "Required kick power is higher than this role's kick power.",
                    grant.ServerId, grant.GroupName));
        }
        foreach (var duplicate in grants.Where(value => value.Enabled)
            .GroupBy(value => (value.ServerId, Name: value.GroupName.ToLowerInvariant()))
            .Where(group => group.Count() > 1))
            issues.Add(new("error", "group.duplicate", $"Rank name '{duplicate.First().GroupName}' is duplicated.",
                duplicate.Key.ServerId, duplicate.First().GroupName));
        foreach (var duplicate in grants.Where(value => value.Enabled)
            .GroupBy(value => (value.ServerId, value.Priority)).Where(group => group.Count() > 1))
            issues.Add(new("warning", "priority.duplicate",
                $"{duplicate.Count()} enabled roles share priority {duplicate.Key.Priority}; list order will break ties.",
                duplicate.Key.ServerId));
        foreach (var grant in grants)
            foreach (var inherited in grant.InheritedGroups ?? [])
                if (!grants.Any(value => value.ServerId == grant.ServerId
                    && value.GroupName.Equals(inherited, StringComparison.OrdinalIgnoreCase)))
                    issues.Add(new("error", "inheritance.missing",
                        $"Inherited rank '{inherited}' does not exist.", grant.ServerId, grant.GroupName));
        foreach (var grant in grants)
        {
            bool HasCycle(string groupName, HashSet<string> path)
            {
                if (!path.Add(groupName)) return true;
                var current = grants.FirstOrDefault(value => value.ServerId == grant.ServerId
                    && value.GroupName.Equals(groupName, StringComparison.OrdinalIgnoreCase));
                foreach (var parent in current?.InheritedGroups ?? [])
                    if (HasCycle(parent, new(path, StringComparer.OrdinalIgnoreCase))) return true;
                return false;
            }
            if (HasCycle(grant.GroupName, new(StringComparer.OrdinalIgnoreCase)))
                issues.Add(new("error", "inheritance.cycle",
                    $"Inheritance cycle detected for rank '{grant.GroupName}'.",
                    grant.ServerId, grant.GroupName));
        }
        return issues;
    }
}
