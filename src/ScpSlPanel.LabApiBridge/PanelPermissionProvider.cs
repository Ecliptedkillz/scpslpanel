using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Permissions;
using LabApi.Features.Wrappers;

namespace ScpSlPanel.LabApiBridge;

/// <summary>
/// Exposes panel-managed custom permission strings through LabAPI's shared
/// permission system. Plugins using Player.HasPermissions/HasAnyPermission
/// automatically consume this provider, just as they do with CedMod.
/// </summary>
public sealed class PanelPermissionProvider : IPermissionsProvider
{
    private static readonly ConcurrentDictionary<string, string[]> PlayerPermissions =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void Set(string userId, IEnumerable<string>? permissions)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        PlayerPermissions[userId] = permissions?
            .Select(permission => permission.Trim())
            .Where(permission => permission.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
    }

    internal static void Clear(string? userId)
    {
        if (!string.IsNullOrWhiteSpace(userId))
            PlayerPermissions.TryRemove(userId, out _);
    }

    public string[] GetPermissions(Player player) =>
        player != null && !string.IsNullOrWhiteSpace(player.UserId)
            && PlayerPermissions.TryGetValue(player.UserId, out var permissions)
            ? permissions.ToArray()
            : Array.Empty<string>();

    public bool HasPermissions(Player player, params string[] permissions)
    {
        if (player == null) return false;
        if (player.IsHost) return true;
        var granted = GetPermissions(player);
        return permissions != null && permissions.All(permission => IsGranted(granted, permission));
    }

    public bool HasAnyPermission(Player player, params string[] permissions)
    {
        if (player == null) return false;
        if (player.IsHost) return true;
        var granted = GetPermissions(player);
        return permissions != null && permissions.Any(permission => IsGranted(granted, permission));
    }

    public void AddPermissions(Player player, params string[] permissions)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.UserId)) return;
        Set(player.UserId, GetPermissions(player).Concat(permissions ?? Array.Empty<string>()));
    }

    public void RemovePermissions(Player player, params string[] permissions)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.UserId)) return;
        var removed = new HashSet<string>(permissions ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        Set(player.UserId, GetPermissions(player).Where(permission => !removed.Contains(permission)));
    }

    private static bool IsGranted(IEnumerable<string> grantedPermissions, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return false;
        foreach (var granted in grantedPermissions)
        {
            if (granted is "*" or ".*") return true;
            if (granted.Equals(requested, StringComparison.OrdinalIgnoreCase)) return true;
            if (!granted.EndsWith(".*", StringComparison.Ordinal)) continue;
            var prefix = granted.Substring(0, granted.Length - 1);
            if (requested.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
