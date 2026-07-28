using System.ComponentModel;

namespace ScpSlPanel.LabApiBridge;

public sealed class BridgeConfig
{
    [Description("Public or LAN URL of SCP Control as seen from this game server.")]
    public string PanelUrl { get; set; } = "http://127.0.0.1:5080";

    [Description("Server ID shown on the SCP Control bridge installation page.")]
    public string ServerId { get; set; } = "";

    [Description("Secret token shown on the SCP Control bridge installation page.")]
    public string Token { get; set; } = "";

    [Description("Number of seconds between bridge heartbeats. Minimum is 2.")]
    public int HeartbeatSeconds { get; set; } = 5;

    [Description("If true, User IDs and IP addresses are omitted for players using Do Not Track.")]
    public bool RespectDoNotTrack { get; set; } = true;
}
