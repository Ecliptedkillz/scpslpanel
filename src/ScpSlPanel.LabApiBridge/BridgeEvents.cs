using System;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.CustomHandlers;

namespace ScpSlPanel.LabApiBridge;

internal sealed class BridgeEvents(BridgeClient client) : CustomEventsHandler
{
    public override void OnPlayerJoined(PlayerJoinedEventArgs ev)
    {
        client.RecordEvent("join", ev.Player);
        client.CaptureSnapshot();
    }

    public override void OnPlayerLeft(PlayerLeftEventArgs ev)
    {
        client.RecordEvent("leave", ev.Player);
        client.CaptureSnapshot(ev.Player);
    }

    public override void OnPlayerChangedRole(PlayerChangedRoleEventArgs ev) => client.CaptureSnapshot();
    public override void OnPlayerKicked(PlayerKickedEventArgs ev) => client.RecordEvent("kick", ev.Player, ev.Reason);
    public override void OnPlayerBanned(PlayerBannedEventArgs ev) =>
        client.RecordEvent("ban", ev.Player, ev.Reason, (int)Math.Min(int.MaxValue, ev.Duration));
    public override void OnPlayerMuted(PlayerMutedEventArgs ev) => client.RecordEvent("mute", ev.Player);
    public override void OnPlayerUnmuted(PlayerUnmutedEventArgs ev) => client.RecordEvent("unmute", ev.Player);
    public override void OnServerWaitingForPlayers() => client.SetRoundState("waiting");
    public override void OnServerRoundStarting(RoundStartingEventArgs ev) => client.SetRoundState("starting");
    public override void OnServerRoundStarted()
    {
        client.SetRoundState("active");
        client.RecordEvent("round-start");
    }
    public override void OnServerRoundEnding(RoundEndingEventArgs ev) => client.SetRoundState("ending");
    public override void OnServerRoundEnded(RoundEndedEventArgs ev)
    {
        client.SetRoundState("ended");
        client.RecordEvent("round-end", detail: ev.LeadingTeam.ToString());
    }
    public override void OnServerRoundRestarted() => client.SetRoundState("waiting");
}
