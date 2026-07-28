using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;

namespace ScpSlPanel.LabApiBridge;

internal sealed class BridgeEvents(BridgeClient client) : CustomEventsHandler
{
    public override void OnPlayerJoined(PlayerJoinedEventArgs ev) => client.CaptureSnapshot();

    public override void OnPlayerLeft(PlayerLeftEventArgs ev) => client.CaptureSnapshot(ev.Player);

    public override void OnPlayerChangedRole(PlayerChangedRoleEventArgs ev) => client.CaptureSnapshot();
}
