using System;
using System.Linq;
using LabApi.Events.CustomHandlers;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Enums;

namespace ScpSlPanel.LabApiBridge;

public sealed class BridgePlugin : Plugin<BridgeConfig>
{
    private BridgeClient? _client;
    private BridgeEvents? _events;

    public override string Name => "SCP Control Bridge";
    public override string Description => "Secure live player telemetry bridge for the SCP Control panel.";
    public override string Author => "Ecliptedkillz";
    public override Version Version => new(0, 2, 1);
    public override Version RequiredApiVersion { get; } = new(LabApiProperties.CompiledVersion);
    public override bool IsTransparent => true;
    public override string ConfigFileName { get; set; } = "scp-control-bridge.yml";

    public override void Enable()
    {
        if (!Guid.TryParse(Config.ServerId, out _) || string.IsNullOrWhiteSpace(Config.Token))
            Logger.Warn("SCP Control Bridge needs ServerId and Token in scp-control-bridge.yml.");
        _client = new BridgeClient(Config);
        _events = new BridgeEvents(_client);
        CustomHandlersManager.RegisterEventsHandler(_events);
        ServerEvents.CommandExecuted += OnCommandExecuted;
        Logger.Info("SCP Control Bridge enabled.");
    }

    public override void Disable()
    {
        ServerEvents.CommandExecuted -= OnCommandExecuted;
        if (_events is not null) CustomHandlersManager.UnregisterEventsHandler(_events);
        _client?.Dispose();
        _events = null;
        _client = null;
    }

    private void OnCommandExecuted(CommandExecutedEventArgs ev)
    {
        if (_client is null || !ev.ExecutedSuccessfully || ev.CommandType != CommandType.RemoteAdmin)
            return;
        var command = (ev.Command?.Command ?? ev.CommandName).Trim().ToLowerInvariant();
        if (command is not ("unban" or "uban")) return;
        var values = ev.Arguments.ToArray();
        var offset = values.Length > 0
            && values[0].Equals(command, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (values.Length <= offset) return;
        var target = values[offset];
        var actor = ev.Sender is null ? "Server Console" : ev.Sender.LogName;
        _client.RecordModerationEvent("unban", null, target, target,
            "Ban removed through Remote Admin", null, actor);
    }
}
