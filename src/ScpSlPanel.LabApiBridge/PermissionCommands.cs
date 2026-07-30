using System;
using System.Linq;
using System.Reflection;
using CommandSystem;
using LabApi.Features.Permissions;
using LabApi.Features.Wrappers;

namespace ScpSlPanel.LabApiBridge;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class PanelRoleCommand : ICommand
{
    public string Command => "panelrole";
    public string[] Aliases => new[] { "panelperms", "prole" };
    public string Description => "Shows a player's SCP Control runtime group and plugin permissions.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.HasPermission("scpcontrol.permissions"))
        {
            response = "Missing permission: scpcontrol.permissions";
            return false;
        }
        if (arguments.Count != 1)
        {
            response = "Usage: panelrole <player id / Steam ID>";
            return false;
        }
        var target = arguments.Array![arguments.Offset];
        var player = Player.ReadyList.FirstOrDefault(value =>
            value.PlayerId.ToString() == target || value.UserId == target);
        if (player == null)
        {
            response = "Player not found.";
            return false;
        }
        var permissions = new PanelPermissionProvider().GetPermissions(player);
        response = $"Player: {player.DisplayName}\nUser ID: {player.UserId}\n"
            + $"Runtime group: {player.PermissionsGroupName ?? "none"}\n"
            + $"Plugin permissions ({permissions.Length}): "
            + (permissions.Length == 0 ? "none" : string.Join(", ", permissions));
        return true;
    }
}

[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class PanelRoleSyncCommand : ICommand
{
    public string Command => "panelrolesync";
    public string[] Aliases => new[] { "permsync" };
    public string Description => "Refreshes SCP Control Discord roles for one player or everyone.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.HasPermission("scpcontrol.permissions"))
        {
            response = "Missing permission: scpcontrol.permissions";
            return false;
        }
        if (BridgePlugin.ActiveClient == null)
        {
            response = "SCP Control Bridge is unavailable.";
            return false;
        }
        if (arguments.Count != 1)
        {
            response = "Usage: panelrolesync <player id / Steam ID / all>";
            return false;
        }
        var target = arguments.Array![arguments.Offset];
        var players = target.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? Player.ReadyList.Where(value => !value.IsHost).ToArray()
            : Player.ReadyList.Where(value => value.PlayerId.ToString() == target
                || value.UserId == target).ToArray();
        if (players.Length == 0)
        {
            response = "No matching online players.";
            return false;
        }
        foreach (var player in players) BridgePlugin.ActiveClient.CheckDiscordGameRole(player);
        response = $"Queued SCP Control role synchronization for {players.Length} player(s).";
        return true;
    }
}

[CommandHandler(typeof(ClientCommandHandler))]
public sealed class PlayerChangeTagCommand : ICommand
{
    public string Command => "changetag";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "Lists or selects one of your available tags.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        var client = BridgePlugin.ActiveClient;
        if (client == null)
        {
            response = "SCP Control Bridge is unavailable.";
            return false;
        }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var senderHub = sender.GetType().GetProperty("ReferenceHub", flags)?.GetValue(sender)
            ?? sender.GetType().GetField("ReferenceHub", flags)?.GetValue(sender)
            ?? sender.GetType().GetField("_hub", flags)?.GetValue(sender);
        if (senderHub == null)
        {
            response = "This command can only be used by an in-game player.";
            return false;
        }
        var player = Player.ReadyList.FirstOrDefault(value =>
            ReferenceEquals(value.ReferenceHub, senderHub));
        if (player == null)
        {
            response = "Your authenticated player could not be found.";
            return false;
        }
        if (arguments.Count > 1)
        {
            response = "Usage: .changetag [number | default]";
            return false;
        }
        var selection = arguments.Count == 0 ? null : arguments.Array![arguments.Offset];
        response = client.TagCommand(player, selection);
        return true;
    }
}
