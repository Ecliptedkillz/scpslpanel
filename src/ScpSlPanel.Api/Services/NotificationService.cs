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
        if (!settings.DiscordBotEnabled || string.IsNullOrWhiteSpace(settings.DiscordBotToken)
            || string.IsNullOrWhiteSpace(settings.DiscordNotificationChannelId)) return;
        var channelId = category switch {
            "moderation" when !string.IsNullOrWhiteSpace(settings.DiscordModerationChannelId) => settings.DiscordModerationChannelId,
            "audit" when !string.IsNullOrWhiteSpace(settings.DiscordAuditChannelId) => settings.DiscordAuditChannelId,
            _ => settings.DiscordNotificationChannelId
        };
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
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) { logger.LogWarning(ex, "Discord notification failed"); }
    }

    public Task TestAsync() => SendAsync("SCP Control connected", "Discord notifications are configured correctly.");
}
