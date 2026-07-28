using System;
using LabApi.Events.CustomHandlers;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;

namespace ScpSlPanel.LabApiBridge;

public sealed class BridgePlugin : Plugin<BridgeConfig>
{
    private BridgeClient? _client;
    private BridgeEvents? _events;

    public override string Name => "SCP Control Bridge";
    public override string Description => "Secure live player telemetry bridge for the SCP Control panel.";
    public override string Author => "Ecliptedkillz";
    public override Version Version => new(0, 1, 0);
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
        Logger.Info("SCP Control Bridge enabled.");
    }

    public override void Disable()
    {
        if (_events is not null) CustomHandlersManager.UnregisterEventsHandler(_events);
        _client?.Dispose();
        _events = null;
        _client = null;
    }
}
