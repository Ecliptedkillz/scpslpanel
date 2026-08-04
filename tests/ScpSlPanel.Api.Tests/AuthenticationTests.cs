using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ScpSlPanel.Api.Tests;

public sealed class PanelFactory : WebApplicationFactory<Program>
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "scp-panel-tests", Guid.NewGuid().ToString("N"));
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) =>
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Panel:DataPath"] = _data,
            ["Panel:BootstrapUsername"] = "test-owner",
            ["Panel:BootstrapPassword"] = "correct-horse-battery-staple",
            ["Panel:Backups:Enabled"] = "false",
            ["Panel:Backups:EncryptionKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray())
        }));
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (Directory.Exists(_data)) Directory.Delete(_data, true); }
}

public sealed class AuthenticationTests(PanelFactory factory) : IClassFixture<PanelFactory>
{
    [Fact]
    public async Task Owner_routes_reject_anonymous_requests()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/system/health")).StatusCode);
    }

    [Theory]
    [InlineData("/api/users")]
    [InlineData("/api/audit")]
    [InlineData("/api/system/backups")]
    [InlineData("/api/system/update/preflight")]
    [InlineData("/api/integrations/discord/diagnostics")]
    public async Task Security_sensitive_routes_reject_anonymous_requests(string path)
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public void Every_non_public_api_endpoint_has_authorization_metadata()
    {
        var publicRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/api/health", "/api/auth/login", "/api/auth/discord/status", "/api/auth/discord/login",
            "/api/auth/discord/callback"
        };
        var endpoints = factory.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api", StringComparison.OrdinalIgnoreCase) == true);
        var missing = endpoints.Where(endpoint =>
        {
            var route = endpoint.RoutePattern.RawText!;
            return !publicRoutes.Contains(route) && !route.StartsWith("/api/bridge/", StringComparison.OrdinalIgnoreCase)
                && endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0;
        }).Select(endpoint => endpoint.RoutePattern.RawText).Distinct().ToArray();
        Assert.True(missing.Length == 0, "Routes missing authorization: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task Unsafe_authenticated_requests_require_panel_csrf_header()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { username = "test-owner", password = "correct-horse-battery-staple" })).EnsureSuccessStatusCode();
        var response = await client.PutAsJsonAsync("/api/users/me/preferences", new
        {
            favoriteServerIds = Array.Empty<Guid>(), dashboardWidgets = new[] { "status" }, notificationsReadAt = (DateTimeOffset?)null
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_creates_session_and_sensitive_action_requires_fresh_confirmation()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Panel-Request", "1");
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "test-owner", password = "correct-horse-battery-staple" });
        login.EnsureSuccessStatusCode();
        var me = await client.GetAsync("/api/auth/me");
        me.EnsureSuccessStatusCode();
        using var unlink = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/discord/link");
        unlink.Headers.Add("X-Panel-Request", "1");
        Assert.Equal((HttpStatusCode)428, (await client.SendAsync(unlink)).StatusCode);
        var confirm = await client.PostAsJsonAsync("/api/auth/reauthenticate", new { password = "correct-horse-battery-staple", code = "" });
        confirm.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Invalid_password_is_rejected()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "test-owner", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Preferences_round_trip_for_authenticated_user()
    {
        using var client = await AuthenticatedClient();
        var save = await client.PutAsJsonAsync("/api/users/me/preferences", new
        {
            favoriteServerIds = Array.Empty<Guid>(), dashboardWidgets = new[] { "status", "activity" }, notificationsReadAt = (DateTimeOffset?)null
        });
        save.EnsureSuccessStatusCode();
        var value = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/users/me/preferences");
        Assert.Equal(2, value.GetProperty("dashboardWidgets").GetArrayLength());
    }

    [Fact]
    public async Task Recovery_backup_is_encrypted_and_verifiable()
    {
        using var client = await AuthenticatedClient();
        var created = await client.PostAsync("/api/system/backups", null);
        created.EnsureSuccessStatusCode();
        var value = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(value.GetProperty("encrypted").GetBoolean());
        Assert.True(value.GetProperty("verified").GetBoolean());
        Assert.EndsWith(".aes", value.GetProperty("fileName").GetString());
    }

    private async Task<HttpClient> AuthenticatedClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Panel-Request", "1");
        (await client.PostAsJsonAsync("/api/auth/login", new { username = "test-owner", password = "correct-horse-battery-staple" })).EnsureSuccessStatusCode();
        return client;
    }
}
