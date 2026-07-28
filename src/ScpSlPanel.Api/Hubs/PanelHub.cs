using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ScpSlPanel.Api.Hubs;

[Authorize]
public sealed class PanelHub : Hub
{
    public Task JoinServer(Guid serverId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"server:{serverId}");

    public Task LeaveServer(Guid serverId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"server:{serverId}");
}
