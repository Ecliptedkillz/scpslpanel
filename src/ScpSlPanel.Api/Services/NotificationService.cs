using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed class NotificationService(
    JsonStore store, IDataProtectionProvider protectionProvider, ILogger<NotificationService> logger)
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("ScpSlPanel.IntegrationSecrets.v1");
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    public const string SecretMask = "••••••••";

    public async Task<PanelIntegrationSettings> GetAsync()
    {
        var value = (await store.ReadAsync<PanelIntegrationSettings>("integrations")).FirstOrDefault() ?? new();
        return value with {
            DiscordWebhookUrl = Unprotect(value.DiscordWebhookUrl),
            DiscordBotToken = Unprotect(value.DiscordBotToken),
            SteamWebApiKey = Unprotect(value.SteamWebApiKey)
        };
    }

    public Task SaveAsync(PanelIntegrationSettings settings) => store.WriteAsync("integrations", [settings with {
        DiscordWebhookUrl = Protect(settings.DiscordWebhookUrl),
        DiscordBotToken = Protect(settings.DiscordBotToken),
        SteamWebApiKey = Protect(settings.SteamWebApiKey)
    }]);

    public async Task<PanelIntegrationSettings> ForClientAsync()
    {
        var value = await GetAsync();
        return value with {
            DiscordWebhookUrl = string.IsNullOrWhiteSpace(value.DiscordWebhookUrl) ? "" : SecretMask,
            DiscordBotToken = string.IsNullOrWhiteSpace(value.DiscordBotToken) ? "" : SecretMask,
            SteamWebApiKey = string.IsNullOrWhiteSpace(value.SteamWebApiKey) ? "" : SecretMask
        };
    }

    public async Task SaveFromClientAsync(PanelIntegrationSettings incoming)
    {
        var existing = await GetAsync();
        await SaveAsync(incoming with {
            DiscordWebhookUrl = incoming.DiscordWebhookUrl == SecretMask ? existing.DiscordWebhookUrl : incoming.DiscordWebhookUrl,
            DiscordBotToken = incoming.DiscordBotToken == SecretMask ? existing.DiscordBotToken : incoming.DiscordBotToken,
            SteamWebApiKey = incoming.SteamWebApiKey == SecretMask ? existing.SteamWebApiKey : incoming.SteamWebApiKey
        });
    }

    private string Protect(string value) =>
        string.IsNullOrWhiteSpace(value) || value.StartsWith("protected:", StringComparison.Ordinal)
            ? value : "protected:" + _protector.Protect(value);
    private string Unprotect(string value)
    {
        if (!value.StartsWith("protected:", StringComparison.Ordinal)) return value;
        try { return _protector.Unprotect(value["protected:".Length..]); }
        catch (Exception ex) { logger.LogError(ex, "Unable to decrypt an integration secret"); return ""; }
    }

    public static string Format(string template, params (string Name, object? Value)[] values)
    {
        var message = template;
        foreach (var (name, value) in values)
            message = message.Replace($"{{{name}}}", Convert.ToString(value,
                System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        return message;
    }

    public async Task SendAsync(string title, string message, string severity = "info", string category = "technical")
    {
        var settings = await GetAsync();
        if (!settings.DiscordBotEnabled || string.IsNullOrWhiteSpace(settings.DiscordBotToken)) return;
        var channelId = category switch {
            "moderation" when !string.IsNullOrWhiteSpace(settings.DiscordModerationChannelId) => settings.DiscordModerationChannelId,
            "audit" when !string.IsNullOrWhiteSpace(settings.DiscordAuditChannelId) => settings.DiscordAuditChannelId,
            _ => settings.DiscordNotificationChannelId
        };
        if (string.IsNullOrWhiteSpace(channelId)) return;
        var delivery = new NotificationDelivery(Guid.NewGuid(), DateTimeOffset.UtcNow, category, severity,
            title, message, channelId, "pending", 0, null);
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var color = severity == "error" ? 15158332 : severity == "warning" ? 16753920 : 5763719;
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"https://discord.com/api/v10/channels/{channelId}/messages");
                request.Headers.Authorization = new("Bot", settings.DiscordBotToken);
                request.Content = JsonContent.Create(new
                {
                    embeds = new[] { new { title, description = message, color, timestamp = DateTimeOffset.UtcNow } }
                });
                using var response = await _http.SendAsync(request);
                if ((int)response.StatusCode == 429)
                {
                    var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt);
                    await Task.Delay(retry > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : retry);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                await RecordAsync(delivery with { Status = "delivered", Attempts = attempt });
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt));
            }
        }
        logger.LogWarning(lastError, "Discord notification failed");
        await RecordAsync(delivery with { Status = "failed", Attempts = 3, Error = lastError?.Message });
    }

    public Task TestAsync() => SendAsync("SCP Control connected", "Discord notifications are configured correctly.");

    public async Task<IReadOnlyList<NotificationDelivery>> HistoryAsync(int take = 100) =>
        (await store.ReadAsync<NotificationDelivery>("notification-history"))
            .OrderByDescending(x => x.At).Take(Math.Clamp(take, 1, 500)).ToArray();

    private async Task RecordAsync(NotificationDelivery delivery)
    {
        await _historyGate.WaitAsync();
        try
        {
            var values = await store.ReadAsync<NotificationDelivery>("notification-history");
            values.Add(delivery);
            await store.WriteAsync("notification-history", values.OrderByDescending(x => x.At).Take(1000));
        }
        finally { _historyGate.Release(); }
    }
}
