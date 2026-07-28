using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;

namespace ScpSlPanel.LabApiBridge;

internal sealed class BridgeClient : IDisposable
{
    private readonly BridgeConfig _config;
    private readonly HttpClient _http = new();
    private readonly Timer _timer;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _snapshotGate = new();
    private HeartbeatPayload _snapshot = new();
    private bool _disposed;

    public BridgeClient(BridgeConfig config)
    {
        _config = config;
        _http.Timeout = TimeSpan.FromSeconds(8);
        CaptureSnapshot();
        var interval = TimeSpan.FromSeconds(Math.Max(2, config.HeartbeatSeconds));
        _timer = new Timer(_ => _ = SendAsync(), null, TimeSpan.Zero, interval);
    }

    public void CaptureSnapshot(Player? excluded = null)
    {
        var players = Player.ReadyList
            .Where(player => !player.IsHost && player != excluded)
            .Select(player =>
            {
                var hideIdentity = _config.RespectDoNotTrack && player.DoNotTrack;
                return new PlayerPayload
                {
                    PlayerId = player.PlayerId,
                    DisplayName = player.DisplayName ?? "Unknown",
                    UserId = hideIdentity ? "" : player.UserId ?? "",
                    IpAddress = hideIdentity ? "" : player.IpAddress ?? "",
                    Role = player.Role.ToString(),
                };
            }).ToList();

        var snapshot = new HeartbeatPayload
        {
            BridgeVersion = typeof(BridgePlugin).Assembly.GetName().Version.ToString(),
            ApiVersion = LabApiProperties.CompiledVersion,
            RoundState = "active",
            MaxPlayers = CustomNetworkManager.slots,
            Players = players,
        };
        lock (_snapshotGate) _snapshot = snapshot;
        _ = SendAsync();
    }

    private async Task SendAsync()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_config.PanelUrl)
            || string.IsNullOrWhiteSpace(_config.ServerId) || string.IsNullOrWhiteSpace(_config.Token))
            return;
        if (!await _sendGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            HeartbeatPayload snapshot;
            lock (_snapshotGate) snapshot = _snapshot;
            var serializer = new DataContractJsonSerializer(typeof(HeartbeatPayload));
            byte[] body;
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, snapshot);
                body = stream.ToArray();
            }
            var endpoint = $"{_config.PanelUrl.TrimEnd('/')}/api/bridge/{Uri.EscapeDataString(_config.ServerId)}/heartbeat";
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.TryAddWithoutValidation("X-Bridge-Token", _config.Token);
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using (var response = await _http.SendAsync(request).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        Logger.Warn($"SCP Control heartbeat rejected: {(int)response.StatusCode} {response.ReasonPhrase}");
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Warn($"SCP Control heartbeat failed: {exception.Message}");
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _http.Dispose();
        _sendGate.Dispose();
    }
}
