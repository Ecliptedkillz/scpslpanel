using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Hubs;

[Authorize]
public sealed class PanelHub(JsonStore store) : Hub
{
    public async Task JoinServer(Guid serverId)
    {
        if (!Guid.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            throw new HubException("Unauthorized.");
        var user = (await store.ReadAsync<PanelUser>("users")).FirstOrDefault(x => x.Id == userId && x.Enabled);
        var grant = user?.ServerAccess?.FirstOrDefault(x => x.ServerId == serverId);
        var permissions = grant?.Permissions ?? user?.Permissions;
        if (user is null || (user.Role != "Owner"
            && (!(grant is not null || (user.ServerIds?.Contains(serverId) ?? false))
                || !(permissions?.Contains("console.view", StringComparer.OrdinalIgnoreCase) ?? false))))
            throw new HubException("You do not have console access for this server.");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"server:{serverId}");
    }

    public Task LeaveServer(Guid serverId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"server:{serverId}");
}
