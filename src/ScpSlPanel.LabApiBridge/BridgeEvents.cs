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
        client.CheckPanelBan(ev.Player);
        client.CheckDiscordGameRole(ev.Player);
        client.CaptureSnapshot();
    }

    public override void OnPlayerLeft(PlayerLeftEventArgs ev)
    {
        PanelPermissionProvider.Clear(ev.Player?.UserId);
        client.ClearTagSession(ev.Player?.UserId);
        client.RecordEvent("leave", ev.Player);
        client.CaptureSnapshot(ev.Player);
    }

    public override void OnPlayerChangedRole(PlayerChangedRoleEventArgs ev) => client.CaptureSnapshot();
    public override void OnPlayerKicked(PlayerKickedEventArgs ev) =>
        client.RecordModerationEvent("kick", ev.Player, ev.Player.UserId, ev.Player.DisplayName,
            ev.Reason, null, Actor(ev.Issuer));
    public override void OnPlayerBanned(PlayerBannedEventArgs ev) =>
        client.RecordModerationEvent(ev.Player is null ? "oban" : "ban", ev.Player, ev.PlayerId,
            ev.Player?.DisplayName, ev.Reason, (int)Math.Min(int.MaxValue, ev.Duration), Actor(ev.Issuer));
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

    private static string Actor(LabApi.Features.Wrappers.Player? player) =>
        player is null ? "Server Console"
        : string.IsNullOrWhiteSpace(player.UserId) ? player.DisplayName
        : $"{player.DisplayName} ({player.UserId})";
}
