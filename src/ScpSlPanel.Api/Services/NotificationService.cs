using System.Net.Http.Json;
using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed class NotificationService(JsonStore store, ILogger<NotificationService> logger)
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<PanelIntegrationSettings> GetAsync() =>
        (await store.ReadAsync<PanelIntegrationSettings>("integrations")).FirstOrDefault() ?? new();

    public Task SaveAsync(PanelIntegrationSettings settings) =>
        store.WriteAsync("integrations", [settings]);

    public static string Format(string template, params (string Name, object? Value)[] values)
    {
        var message = template;
        foreach (var (name, value) in values)
            message = message.Replace($"{{{name}}}", Convert.ToString(value,
                System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        return message;
    }

    public async Task SendAsync(string title, string message, string severity = "info")
    {
        var settings = await GetAsync();
        if (!settings.DiscordBotEnabled || string.IsNullOrWhiteSpace(settings.DiscordBotToken)
            || settings.DiscordNotificationChannelId == 0) return;
        try
        {
            var color = severity == "error" ? 15158332 : severity == "warning" ? 16753920 : 5763719;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://discord.com/api/v10/channels/{settings.DiscordNotificationChannelId}/messages");
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
