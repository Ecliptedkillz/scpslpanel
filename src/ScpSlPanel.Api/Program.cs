using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Hubs;
using ScpSlPanel.Api.Infrastructure;
using ScpSlPanel.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.TimestampFormat = "HH:mm:ss ");
var dataPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Panel:DataPath"] ?? "data"));
Directory.CreateDirectory(dataPath);
builder.Services.AddDataProtection()
    .SetApplicationName("ScpSlPanel")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
builder.Services.AddSingleton<JsonStore>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<BridgeStateService>();
builder.Services.AddSingleton<BridgeCommandService>();
builder.Services.AddSingleton<PlayerDataService>();
builder.Services.AddSingleton<DiscordLinkService>();
builder.Services.AddSingleton<OperationsDataService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<DiscordBotService>();
builder.Services.AddSingleton<MaintenanceService>();
builder.Services.AddSingleton<RestartCoordinator>();
builder.Services.AddSingleton<ServerManager>();
builder.Services.AddHostedService<BootstrapService>();
builder.Services.AddHostedService<SchedulerService>();
builder.Services.AddHostedService<MonitoringService>();
builder.Services.AddHostedService<DailyReportService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DiscordBotService>());
builder.Services.AddSignalR();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "scpsl_panel";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    options.Events.OnValidatePrincipal = async context =>
    {
        var idValue = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var versionValue = context.Principal?.FindFirstValue("session_version");
        if (!Guid.TryParse(idValue, out var id) || !int.TryParse(versionValue, out var version))
        {
            context.RejectPrincipal();
            return;
        }
        var store = context.HttpContext.RequestServices.GetRequiredService<JsonStore>();
        var user = (await store.ReadAsync<PanelUser>("users")).FirstOrDefault(x => x.Id == id);
        if (user is null || !user.Enabled || user.SessionVersion != version) context.RejectPrincipal();
    };
});
builder.Services.AddAuthorization(options =>
    options.AddPolicy("Owner", policy => policy.RequireRole("Owner")));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Panel:AllowedHosts").Get<string[]>() ?? [])
        .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("login", limiter =>
{
    limiter.PermitLimit = 8;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.QueueLimit = 0;
    limiter.AutoReplenishment = true;
}));

var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Request failed");
        context.Response.StatusCode = ex switch
        {
            KeyNotFoundException => 404,
            ArgumentException => 400,
            InvalidOperationException => 409,
            FileNotFoundException => 422,
            _ => 500
        };
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

static string Actor(ClaimsPrincipal user) => user.Identity?.Name ?? "unknown";
static IReadOnlyList<(string Framework, string Path)> PluginRoots(ServerDefinition server)
{
    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    return
    [
        ("EXILED", Path.Combine(server.WorkingDirectory, "EXILED", "Plugins")),
        ("NWAPI", Path.Combine(server.WorkingDirectory, "PluginAPI", "plugins")),
        ("LabAPI", Path.Combine(server.WorkingDirectory, "AppData", "SCP Secret Laboratory", "LabAPI", "plugins")),
        ("LabAPI", Path.Combine(appData, "SCP Secret Laboratory", "LabAPI", "plugins"))
    ];
}

static IReadOnlyList<string> PluginConfigRoots(ServerDefinition server)
{
    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    return
    [
        Path.Combine(server.WorkingDirectory, "EXILED", "Configs"),
        Path.Combine(server.WorkingDirectory, "PluginAPI", "configs"),
        Path.Combine(server.WorkingDirectory, "AppData", "SCP Secret Laboratory", "LabAPI", "configs", server.QueryPort.ToString()),
        Path.Combine(appData, "SCP Secret Laboratory", "LabAPI", "configs", server.QueryPort.ToString())
    ];
}

static string EnsurePathInRoots(string requestedPath, IEnumerable<string> roots)
{
    var fullPath = Path.GetFullPath(requestedPath);
    var valid = roots.Where(Directory.Exists).Any(root =>
        fullPath.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    if (!valid) throw new ArgumentException("The requested plugin file is outside this server's plugin directories.");
    return fullPath;
}

static string NormalizePluginName(string value) =>
    string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

static async Task<PanelUser?> CurrentUser(ClaimsPrincipal principal, JsonStore store)
{
    if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return null;
    return (await store.ReadAsync<PanelUser>("users")).FirstOrDefault(x => x.Id == id && x.Enabled);
}

static async Task<bool> Can(ClaimsPrincipal principal, JsonStore store, Guid serverId, string permission)
{
    var account = await CurrentUser(principal, store);
    var grant = account?.ServerAccess?.FirstOrDefault(x => x.ServerId == serverId);
    var permissions = grant?.Permissions ?? account?.Permissions ?? [];
    return account is not null && (account.Role == "Owner"
        || ((grant is not null || (account.ServerIds?.Contains(serverId) ?? false))
            && permissions.Contains(permission, StringComparer.OrdinalIgnoreCase)));
}

app.MapPost("/api/auth/login", async (LoginRequest request, JsonStore store, PasswordService passwords, TotpService totp, HttpContext context) =>
{
    var user = (await store.ReadAsync<PanelUser>("users"))
        .FirstOrDefault(x => x.Enabled && x.Username.Equals(request.Username, StringComparison.OrdinalIgnoreCase));
    if (user is null || !passwords.Verify(request.Password, user.PasswordHash)) return Results.Unauthorized();
    if (user.TotpEnabled && (string.IsNullOrWhiteSpace(user.TotpSecret) || !totp.Verify(user.TotpSecret, request.Code)))
        return Results.Json(new { error = "A valid two-factor authentication code is required.", requiresTwoFactor = true }, statusCode: 401);
    var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role), new Claim("session_version", user.SessionVersion.ToString()) };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new { user.Id, user.Username, user.Role });
}).RequireRateLimiting("login");
app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();
app.MapGet("/api/auth/me", async (ClaimsPrincipal principal, JsonStore store) =>
{
    var user = await CurrentUser(principal, store);
    return user is null ? Results.Unauthorized() : Results.Ok(new
    {
        user.Username, user.Role, serverIds = user.ServerAccess?.Select(x => x.ServerId).Distinct().ToArray() ?? user.ServerIds ?? [],
        permissions = user.Permissions ?? [], serverAccess = user.ServerAccess ?? [], twoFactorEnabled = user.TotpEnabled
    });
}).RequireAuthorization();
app.MapPost("/api/auth/2fa/setup", async (ClaimsPrincipal principal, JsonStore store, TotpService totp) =>
{
    var users = await store.ReadAsync<PanelUser>("users");
    var index = users.FindIndex(x => x.Id.ToString() == principal.FindFirstValue(ClaimTypes.NameIdentifier));
    if (index < 0) return Results.Unauthorized();
    var secret = totp.GenerateSecret();
    users[index] = users[index] with { TotpSecret = secret, TotpEnabled = false };
    await store.WriteAsync("users", users);
    var issuer = Uri.EscapeDataString("SCP Control");
    var account = Uri.EscapeDataString(users[index].Username);
    return Results.Ok(new { secret, uri = $"otpauth://totp/{issuer}:{account}?secret={secret}&issuer={issuer}" });
}).RequireAuthorization();
app.MapPost("/api/auth/2fa/confirm", async (TotpRequest request, ClaimsPrincipal principal, JsonStore store, TotpService totp) =>
{
    var users = await store.ReadAsync<PanelUser>("users");
    var index = users.FindIndex(x => x.Id.ToString() == principal.FindFirstValue(ClaimTypes.NameIdentifier));
    if (index < 0 || string.IsNullOrWhiteSpace(users[index].TotpSecret) || !totp.Verify(users[index].TotpSecret!, request.Code))
        return Results.BadRequest(new { error = "The verification code is invalid." });
    users[index] = users[index] with { TotpEnabled = true };
    await store.WriteAsync("users", users);
    return Results.NoContent();
}).RequireAuthorization();
app.MapPost("/api/auth/2fa/disable", async (TotpRequest request, ClaimsPrincipal principal, JsonStore store, TotpService totp) =>
{
    var users = await store.ReadAsync<PanelUser>("users");
    var index = users.FindIndex(x => x.Id.ToString() == principal.FindFirstValue(ClaimTypes.NameIdentifier));
    if (index < 0 || !users[index].TotpEnabled || !totp.Verify(users[index].TotpSecret ?? "", request.Code))
        return Results.BadRequest(new { error = "The verification code is invalid." });
    users[index] = users[index] with { TotpEnabled = false, TotpSecret = null };
    await store.WriteAsync("users", users);
    return Results.NoContent();
}).RequireAuthorization();
app.MapPost("/api/auth/sessions/revoke", async (ClaimsPrincipal principal, JsonStore store, HttpContext context) =>
{
    var users = await store.ReadAsync<PanelUser>("users");
    var index = users.FindIndex(x => x.Id.ToString() == principal.FindFirstValue(ClaimTypes.NameIdentifier));
    if (index < 0) return Results.Unauthorized();
    users[index] = users[index] with { SessionVersion = users[index].SessionVersion + 1 };
    await store.WriteAsync("users", users);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();
app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", at = DateTimeOffset.UtcNow }));
app.MapPost("/api/bridge/{serverId:guid}/heartbeat", async (
    Guid serverId, BridgeHeartbeat heartbeat, HttpContext context, ServerManager servers,
    BridgeStateService bridgeState, PlayerDataService playerData, IHubContext<PanelHub> hub) =>
{
    if (!await servers.ValidateBridgeTokenAsync(serverId, context.Request.Headers["X-Bridge-Token"].FirstOrDefault()))
        return Results.Unauthorized();
    bridgeState.Update(serverId, heartbeat);
    await playerData.RecordHeartbeatAsync(serverId, heartbeat);
    await hub.Clients.All.SendAsync("BridgeChanged", serverId);
    return Results.NoContent();
});
app.MapGet("/api/bridge/{serverId:guid}/commands", async (
    Guid serverId, HttpContext context, ServerManager servers, BridgeCommandService commands) =>
{
    if (!await servers.ValidateBridgeTokenAsync(serverId, context.Request.Headers["X-Bridge-Token"].FirstOrDefault()))
        return Results.Unauthorized();
    return Results.Ok(commands.PendingCommands(serverId));
});
app.MapPost("/api/bridge/{serverId:guid}/commands/{commandId:guid}/result", async (
    Guid serverId, Guid commandId, BridgeCommandResult result, HttpContext context,
    ServerManager servers, BridgeCommandService commands) =>
{
    if (!await servers.ValidateBridgeTokenAsync(serverId, context.Request.Headers["X-Bridge-Token"].FirstOrDefault()))
        return Results.Unauthorized();
    return commands.Complete(serverId, commandId, result) ? Results.NoContent() : Results.NotFound();
});
app.MapPost("/api/bridge/{serverId:guid}/events", async (
    Guid serverId, BridgeEventRequest value, HttpContext context, ServerManager servers,
    BridgeCommandService commands, IHubContext<PanelHub> hub) =>
{
    if (!await servers.ValidateBridgeTokenAsync(serverId, context.Request.Headers["X-Bridge-Token"].FirstOrDefault()))
        return Results.Unauthorized();
    await commands.RecordEventAsync(serverId, value);
    await hub.Clients.All.SendAsync("BridgeActivity", serverId);
    return Results.NoContent();
});

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/overview", async (ServerManager servers, AuditService audit, ClaimsPrincipal principal, JsonStore store) =>
{
    var snapshots = await servers.SnapshotsAsync();
    var user = await CurrentUser(principal, store);
    if (user?.Role != "Owner")
        snapshots = snapshots.Where(x => user?.ServerAccess?.Any(g => g.ServerId == x.Id) == true
            || user?.ServerIds?.Contains(x.Id) == true).ToList();
    return new DashboardOverview(snapshots.Count(x => x.State == ServerState.Online), snapshots.Count,
        snapshots.Sum(x => x.Players), snapshots.Sum(x => x.MemoryBytes), snapshots,
        user?.Role == "Owner" ? await audit.RecentAsync(12) : []);
});
api.MapGet("/servers", async (ServerManager servers, ClaimsPrincipal principal, JsonStore store) =>
{
    var values = await servers.SnapshotsAsync();
    var user = await CurrentUser(principal, store);
    return user?.Role == "Owner" ? values : values.Where(x =>
        user?.ServerAccess?.Any(g => g.ServerId == x.Id) == true || user?.ServerIds?.Contains(x.Id) == true);
});
api.MapGet("/servers/{id:guid}", async (Guid id, ServerManager servers, ClaimsPrincipal user, JsonStore store) =>
    !await Can(user, store, id, "view") ? Results.Forbid()
    : await servers.SnapshotAsync(id) is { } value ? Results.Ok(value) : Results.NotFound());
api.MapPost("/servers", async (ServerCreateRequest request, ServerManager servers, AuditService audit, ClaimsPrincipal user) =>
{
    var server = await servers.AddAsync(request);
    await audit.AddAsync(Actor(user), "server.create", server.Name, server.ExecutablePath);
    return Results.Created($"/api/servers/{server.Id}", server);
}).RequireAuthorization("Owner");
api.MapDelete("/servers/{id:guid}", async (Guid id, ServerManager servers, AuditService audit, ClaimsPrincipal user) =>
{
    if (!await servers.RemoveAsync(id)) return Results.Conflict(new { error = "Server is running or does not exist." });
    await audit.AddAsync(Actor(user), "server.delete", id.ToString(), "Removed server definition");
    return Results.NoContent();
}).RequireAuthorization("Owner");
api.MapPost("/servers/{id:guid}/start", async (Guid id, ServerManager servers, ClaimsPrincipal user, JsonStore store) => { if (!await Can(user, store, id, "server.start")) return Results.Forbid(); await servers.StartAsync(id, Actor(user)); return Results.Accepted(); });
api.MapPost("/servers/{id:guid}/stop", async (Guid id, ServerManager servers, ClaimsPrincipal user, JsonStore store) => { if (!await Can(user, store, id, "server.stop")) return Results.Forbid(); await servers.StopAsync(id, Actor(user)); return Results.Accepted(); });
api.MapPost("/servers/{id:guid}/restart", async (Guid id, ServerManager servers, ClaimsPrincipal user, JsonStore store) => { if (!await Can(user, store, id, "server.restart")) return Results.Forbid(); await servers.RestartAsync(id, Actor(user)); return Results.Accepted(); });
api.MapPost("/servers/{id:guid}/restart/countdown", async (Guid id, RestartCountdownRequest request, RestartCoordinator restarts, ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, id, "server.restart")) return Results.Forbid();
    return Results.Accepted(value: restarts.Schedule(id, request.Seconds, request.Message, Actor(user)));
});
api.MapGet("/servers/{id:guid}/restart/countdown", async (Guid id, RestartCoordinator restarts, ClaimsPrincipal user, JsonStore store) =>
    !await Can(user, store, id, "view") ? Results.Forbid() : Results.Ok(restarts.Get(id)));
api.MapDelete("/servers/{id:guid}/restart/countdown", async (Guid id, RestartCoordinator restarts, ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, id, "server.restart")) return Results.Forbid();
    return await restarts.CancelAsync(id, Actor(user)) ? Results.NoContent() : Results.NotFound();
});
api.MapPost("/servers/{id:guid}/kill", async (Guid id, ServerManager servers, ClaimsPrincipal user) => { await servers.StopAsync(id, Actor(user), true); return Results.Accepted(); }).RequireAuthorization("Owner");
api.MapPost("/servers/{id:guid}/command", async (Guid id, CommandRequest request, ServerManager servers, ClaimsPrincipal user, JsonStore store) => { if (!await Can(user, store, id, "console.write")) return Results.Forbid(); await servers.CommandAsync(id, request.Command, Actor(user)); return Results.Accepted(); });
api.MapGet("/servers/{id:guid}/console/history", async (Guid id, int? take, string? search, OperationsDataService operations, ClaimsPrincipal user, JsonStore store) =>
    !await Can(user, store, id, "console.view") ? Results.Forbid()
    : Results.Ok(await operations.ConsoleAsync(id, take ?? 1000, search)));
api.MapGet("/servers/{id:guid}/console/download", async (Guid id, OperationsDataService operations, ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, id, "console.view")) return Results.Forbid();
    var entries = await operations.ConsoleAsync(id, 5000, null);
    var text = string.Join(Environment.NewLine, entries.Select(x => $"[{x.At:O}] [{x.Stream}] {x.Line}"));
    return Results.File(System.Text.Encoding.UTF8.GetBytes(text), "text/plain", $"console-{id}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
});
api.MapGet("/servers/{id:guid}/metrics", async (Guid id, int? hours, OperationsDataService operations, ClaimsPrincipal user, JsonStore store) =>
    !await Can(user, store, id, "monitoring") ? Results.Forbid()
    : Results.Ok(await operations.MetricsAsync(id, hours ?? 24)));
api.MapGet("/servers/{id:guid}/incidents", async (Guid id, OperationsDataService operations, ClaimsPrincipal user, JsonStore store) =>
    !await Can(user, store, id, "monitoring") ? Results.Forbid()
    : Results.Ok(await operations.IncidentsAsync(id)));
api.MapGet("/servers/{id:guid}/players", async (Guid id, BridgeStateService bridge, ClaimsPrincipal user, JsonStore store) =>
    !await Can(user, store, id, "players") ? Results.Forbid() : Results.Ok(bridge.Get(id)));
api.MapGet("/servers/{id:guid}/bridge", async (Guid id, ServerManager servers, BridgeStateService bridge) =>
{
    var token = await servers.EnsureBridgeTokenAsync(id);
    return Results.Ok(new { serverId = id, token, endpoint = $"/api/bridge/{id}/heartbeat", status = bridge.Get(id) });
}).RequireAuthorization("Owner");
api.MapPost("/servers/{id:guid}/bridge/regenerate", async (Guid id, ServerManager servers, AuditService audit, ClaimsPrincipal user) =>
{
    var token = await servers.EnsureBridgeTokenAsync(id, true);
    await audit.AddAsync(Actor(user), "bridge.token.regenerate", id.ToString(), "Regenerated LabAPI bridge token");
    return Results.Ok(new { serverId = id, token, endpoint = $"/api/bridge/{id}/heartbeat" });
}).RequireAuthorization("Owner");
api.MapPost("/servers/{id:guid}/players/{playerId}/kick", async (Guid id, string playerId, ModerationRequest request, BridgeCommandService commands, BridgeStateService bridge, PlayerDataService playerData, ClaimsPrincipal user, JsonStore store, CancellationToken cancellationToken) =>
{
    if (!await Can(user, store, id, "players.kick")) return Results.Forbid();
    if (!bridge.Get(id).Connected) return Results.Conflict(new { error = "The LabAPI bridge must be connected to verify a kick." });
    var reason = request.Reason ?? "Removed by panel";
    var result = await commands.ExecuteAsync(id, "kick", playerId, reason, cancellationToken: cancellationToken);
    if (!result.Success) return Results.Conflict(new { error = result.Message ?? "The game server rejected the kick." });
    var player = bridge.Get(id).Players.FirstOrDefault(x => x.Id == playerId);
    if (player is not null)
        await playerData.RecordModerationAsync(id, string.IsNullOrWhiteSpace(player.UserId) ? $"ip:{player.IpAddress}" : player.UserId,
            player.Nickname, "kick", reason, Actor(user), null);
    return Results.Ok(result);
});
api.MapPost("/servers/{id:guid}/players/{playerId}/ban", async (Guid id, string playerId, ModerationRequest request, BridgeCommandService commands, BridgeStateService bridge, PlayerDataService playerData, ClaimsPrincipal user, JsonStore store, CancellationToken cancellationToken) =>
{
    if (!await Can(user, store, id, "players.ban")) return Results.Forbid();
    var duration = Math.Max(1, request.DurationMinutes ?? 60);
    if (!bridge.Get(id).Connected) return Results.Conflict(new { error = "The LabAPI bridge must be connected to verify a ban." });
    var reason = request.Reason ?? "Banned by panel";
    var result = await commands.ExecuteAsync(id, "ban", playerId, reason, duration * 60, cancellationToken: cancellationToken);
    if (!result.Success) return Results.Conflict(new { error = result.Message ?? "The game server rejected the ban." });
    var player = bridge.Get(id).Players.FirstOrDefault(x => x.Id == playerId);
    if (player is not null)
        await playerData.RecordModerationAsync(id, string.IsNullOrWhiteSpace(player.UserId) ? $"ip:{player.IpAddress}" : player.UserId,
            player.Nickname, "ban", reason, Actor(user), duration);
    return Results.Ok(result);
});
api.MapPost("/servers/{id:guid}/players/{playerId}/mute", async (Guid id, string playerId, ModerationRequest request, BridgeCommandService commands, BridgeStateService bridge, PlayerDataService playerData, ClaimsPrincipal user, JsonStore store, CancellationToken cancellationToken) =>
{
    if (!await Can(user, store, id, "players.mute")) return Results.Forbid();
    if (!bridge.Get(id).Connected) return Results.Conflict(new { error = "The LabAPI bridge must be connected to verify a mute." });
    var reason = request.Reason ?? "Muted by panel";
    var result = await commands.ExecuteAsync(id, "mute", playerId, reason,
        request.DurationMinutes is > 0 ? request.DurationMinutes * 60 : null, cancellationToken: cancellationToken);
    if (!result.Success) return Results.Conflict(new { error = result.Message ?? "The game server rejected the mute." });
    var player = bridge.Get(id).Players.FirstOrDefault(x => x.Id == playerId);
    if (player is not null)
        await playerData.RecordModerationAsync(id, string.IsNullOrWhiteSpace(player.UserId) ? $"ip:{player.IpAddress}" : player.UserId,
            player.Nickname, "mute", reason, Actor(user), request.DurationMinutes);
    return Results.Ok(result);
});
api.MapPost("/servers/{id:guid}/players/{playerId}/unmute", async (Guid id, string playerId, BridgeCommandService commands, BridgeStateService bridge, PlayerDataService playerData, ClaimsPrincipal user, JsonStore store, CancellationToken cancellationToken) =>
{
    if (!await Can(user, store, id, "players.mute")) return Results.Forbid();
    if (!bridge.Get(id).Connected) return Results.Conflict(new { error = "The LabAPI bridge must be connected to verify an unmute." });
    var result = await commands.ExecuteAsync(id, "unmute", playerId, cancellationToken: cancellationToken);
    if (!result.Success) return Results.Conflict(new { error = result.Message ?? "The game server rejected the unmute." });
    var player = bridge.Get(id).Players.FirstOrDefault(x => x.Id == playerId);
    if (player is not null)
        await playerData.RecordModerationAsync(id, string.IsNullOrWhiteSpace(player.UserId) ? $"ip:{player.IpAddress}" : player.UserId,
            player.Nickname, "unmute", "Unmuted from panel", Actor(user), null);
    return Results.Ok(result);
});
api.MapPost("/servers/{id:guid}/announcement", async (Guid id, AnnouncementRequest request, BridgeCommandService commands, BridgeStateService bridge, ClaimsPrincipal user, JsonStore store, CancellationToken cancellationToken) =>
{
    if (!await Can(user, store, id, "announcements")) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(request.Message)) return Results.BadRequest(new { error = "Announcement text is required." });
    if (!bridge.Get(id).Connected) return Results.Conflict(new { error = "The LabAPI bridge is not connected." });
    var result = await commands.ExecuteAsync(id, "announcement", message: request.Message.Trim(),
        durationSeconds: Math.Clamp(request.DurationSeconds, 1, ushort.MaxValue), cancellationToken: cancellationToken);
    return result.Success ? Results.Ok(result) : Results.Conflict(new { error = result.Message });
});
api.MapGet("/servers/{id:guid}/activity", async (Guid id, int? take, BridgeCommandService commands, ClaimsPrincipal user, JsonStore store) =>
    !await Can(user, store, id, "monitoring") ? Results.Forbid() : Results.Ok(await commands.ActivityAsync(id, take ?? 250)));
api.MapGet("/servers/{id:guid}/rounds", async (Guid id, int? take, BridgeCommandService commands, ClaimsPrincipal user, JsonStore store) =>
    !await Can(user, store, id, "monitoring") ? Results.Forbid() : Results.Ok(await commands.RoundsAsync(id, take ?? 100)));
api.MapGet("/servers/{id:guid}/player-history", async (
    Guid id, PlayerDataService players, DiscordLinkService discordLinks, ServerManager servers,
    ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, id, "players.history")) return Results.Forbid();
    var server = await servers.FindAsync(id);
    return server is null ? Results.NotFound()
        : Results.Ok(await discordLinks.EnrichAsync(server, await players.ListAsync(id)));
});
api.MapGet("/servers/{id:guid}/player-history/{playerId:guid}", async (
    Guid id, Guid playerId, PlayerDataService players, DiscordLinkService discordLinks,
    ServerManager servers, ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, id, "players.history")) return Results.Forbid();
    var server = await servers.FindAsync(id);
    var player = await players.FindAsync(id, playerId);
    return server is null || player is null ? Results.NotFound()
        : Results.Ok(await discordLinks.EnrichAsync(server, player));
});
api.MapGet("/players/global", async (
    PlayerDataService players, DiscordLinkService discordLinks, ServerManager servers,
    ClaimsPrincipal user, JsonStore store) =>
{
    var results = new List<object>();
    foreach (var server in await servers.DefinitionsAsync())
    {
        if (!await Can(user, store, server.Id, "players.history")) continue;
        var records = await discordLinks.EnrichAsync(server, await players.ListAsync(server.Id));
        results.AddRange(records.Select(player => (object)new { serverId = server.Id, serverName = server.Name, player }));
    }
    return Results.Ok(results);
});
api.MapGet("/players/identity-health", async (
    DiscordLinkService discordLinks, ServerManager servers, ClaimsPrincipal user, JsonStore store) =>
{
    var results = new List<IdentityLinkHealth>();
    foreach (var server in await servers.DefinitionsAsync())
        if (await Can(user, store, server.Id, "players.history"))
            results.AddRange(discordLinks.Health(server));
    return Results.Ok(results);
});
api.MapPut("/servers/{id:guid}/players/identity-link", async (
    Guid id, IdentityLinkRequest request, ServerManager servers, ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, id, "players.notes")) return Results.Forbid();
    if (!ulong.TryParse(request.SteamId, out _) || !ulong.TryParse(request.DiscordId, out _))
        return Results.BadRequest(new { error = "Steam and Discord IDs must be valid numeric IDs." });
    var server = await servers.FindAsync(id);
    if (server is null) return Results.NotFound();
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCP Secret Laboratory", "LabAPI", "configs", server.QueryPort.ToString(),
        "PlayhousePlugin", "DiscordLinks.csv");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var lines = File.Exists(path) ? (await File.ReadAllLinesAsync(path)).ToList() : [];
    lines.RemoveAll(line => line.Split(',', 2)[0].Trim() == request.SteamId.Trim());
    lines.Add($"{request.SteamId.Trim()},{request.DiscordId.Trim()}");
    await File.WriteAllLinesAsync(path, lines);
    return Results.NoContent();
});
api.MapPost("/servers/{id:guid}/player-history/{playerId:guid}/notes", async (
    Guid id, Guid playerId, PlayerNoteRequest request, PlayerDataService players,
    ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, id, "players.notes")) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(request.Text)) return Results.BadRequest(new { error = "Note text is required." });
    return await players.AddNoteAsync(id, playerId, request.Text, Actor(user)) is { } player
        ? Results.Ok(player) : Results.NotFound();
});
api.MapPost("/servers/{id:guid}/player-history/{playerId:guid}/actions", async (
    Guid id, Guid playerId, PlayerActionRequest request, PlayerDataService players,
    ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, id, "players.actions")) return Results.Forbid();
    var type = request.Type.Trim().ToLowerInvariant();
    if (type is not ("warning" or "watchlist" or "allowlist" or "unmute"))
        return Results.BadRequest(new { error = "Unsupported player action." });
    return await players.AddActionAsync(id, playerId, type, request.Reason, Actor(user), request.DurationMinutes) is { } player
        ? Results.Ok(player) : Results.NotFound();
});
api.MapDelete("/servers/{id:guid}/player-history", async (
    Guid id, int? olderThanDays, PlayerDataService players, ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, id, "maintenance")) return Results.Forbid();
    return Results.Ok(new { removed = await players.CleanupAsync(id, olderThanDays ?? 365) });
});

api.MapGet("/servers/{id:guid}/files/{**path}", async (Guid id, string path, ServerManager servers, JsonStore store, ClaimsPrincipal user) =>
{
    if (!await Can(user, store, id, "config.view")) return Results.Forbid();
    var server = await servers.FindAsync(id);
    if (server is null) return Results.NotFound();
    var full = store.ResolveSafePath(server.WorkingDirectory, path);
    return File.Exists(full) ? Results.Text(await File.ReadAllTextAsync(full), "text/plain") : Results.NotFound();
});
api.MapPut("/servers/{id:guid}/files/{**path}", async (Guid id, string path, ConfigFileRequest request, ServerManager servers, JsonStore store, AuditService audit, ClaimsPrincipal user) =>
{
    if (!await Can(user, store, id, "config.write")) return Results.Forbid();
    var server = await servers.FindAsync(id);
    if (server is null) return Results.NotFound();
    var full = store.ResolveSafePath(server.WorkingDirectory, path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.WriteAllTextAsync(full, request.Content);
    await audit.AddAsync(Actor(user), "file.write", server.Name, path);
    return Results.NoContent();
});

static string ScpConfigRoot(ServerDefinition server) => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "SCP Secret Laboratory", "config", server.QueryPort.ToString());

api.MapGet("/servers/{id:guid}/server-config", async (
    Guid id, ServerManager servers, ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, id, "config.view")) return Results.Forbid();
    var server = await servers.FindAsync(id);
    if (server is null) return Results.NotFound();
    var root = ScpConfigRoot(server);
    Directory.CreateDirectory(root);
    var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(1000).ToArray();
    return Results.Ok(new { server.QueryPort, root, files });
});
api.MapGet("/servers/{id:guid}/server-config/{**path}", async (
    Guid id, string path, ServerManager servers, JsonStore store, ClaimsPrincipal user) =>
{
    if (!await Can(user, store, id, "config.view")) return Results.Forbid();
    var server = await servers.FindAsync(id);
    if (server is null) return Results.NotFound();
    var full = store.ResolveSafePath(ScpConfigRoot(server), path);
    return File.Exists(full) ? Results.Text(await File.ReadAllTextAsync(full), "text/plain") : Results.NotFound();
});
api.MapPut("/servers/{id:guid}/server-config/{**path}", async (
    Guid id, string path, ConfigFileRequest request, ServerManager servers, JsonStore store,
    AuditService audit, ClaimsPrincipal user) =>
{
    if (!await Can(user, store, id, "config.write")) return Results.Forbid();
    var server = await servers.FindAsync(id);
    if (server is null) return Results.NotFound();
    var root = ScpConfigRoot(server);
    Directory.CreateDirectory(root);
    var full = store.ResolveSafePath(root, path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.WriteAllTextAsync(full, request.Content);
    await audit.AddAsync(Actor(user), "server.config.write", server.Name, $"{server.QueryPort}/{path}");
    return Results.NoContent();
});

api.MapGet("/bans", (JsonStore store) => store.ReadAsync<BanEntry>("bans")).RequireAuthorization("Owner");
api.MapPost("/bans", async (ModerationRequest request, JsonStore store, AuditService audit, ClaimsPrincipal user) =>
{
    var bans = await store.ReadAsync<BanEntry>("bans");
    var entry = new BanEntry(Guid.NewGuid(), request.PlayerId, request.PlayerId, request.Reason ?? "No reason provided",
        Actor(user), DateTimeOffset.UtcNow, request.DurationMinutes is > 0 ? DateTimeOffset.UtcNow.AddMinutes(request.DurationMinutes.Value) : null, false);
    bans.Insert(0, entry);
    await store.WriteAsync("bans", bans);
    await audit.AddAsync(Actor(user), "player.ban", request.PlayerId, entry.Reason);
    return Results.Created($"/api/bans/{entry.Id}", entry);
}).RequireAuthorization("Owner");
api.MapDelete("/bans/{id:guid}", async (Guid id, JsonStore store, AuditService audit, ClaimsPrincipal user) =>
{
    var bans = await store.ReadAsync<BanEntry>("bans");
    var index = bans.FindIndex(x => x.Id == id);
    if (index < 0) return Results.NotFound();
    bans[index] = bans[index] with { Revoked = true };
    await store.WriteAsync("bans", bans);
    await audit.AddAsync(Actor(user), "player.unban", bans[index].Target, bans[index].Reason);
    return Results.NoContent();
}).RequireAuthorization("Owner");
api.MapGet("/audit", (int? take, AuditService audit) => audit.RecentAsync(take ?? 100)).RequireAuthorization("Owner");
api.MapGet("/schedules", (JsonStore store) => store.ReadAsync<ScheduleEntry>("schedules")).RequireAuthorization("Owner");
api.MapPost("/schedules", async (ScheduleRequest request, JsonStore store, AuditService audit, ClaimsPrincipal user) =>
{
    var schedules = await store.ReadAsync<ScheduleEntry>("schedules");
    var item = new ScheduleEntry(Guid.NewGuid(), request.ServerId, request.Name, request.Cron, request.Action,
        request.Enabled, null, Math.Clamp(request.WarningSeconds, 0, 86400));
    schedules.Add(item);
    await store.WriteAsync("schedules", schedules);
    await audit.AddAsync(Actor(user), "schedule.create", item.Name, $"{item.Cron}: {item.Action}");
    return Results.Created($"/api/schedules/{item.Id}", item);
}).RequireAuthorization("Owner");
api.MapDelete("/schedules/{id:guid}", async (Guid id, JsonStore store) =>
{
    var schedules = await store.ReadAsync<ScheduleEntry>("schedules");
    if (schedules.RemoveAll(x => x.Id == id) == 0) return Results.NotFound();
    await store.WriteAsync("schedules", schedules);
    return Results.NoContent();
}).RequireAuthorization("Owner");
api.MapGet("/plugins/{serverId:guid}", async (Guid serverId, ServerManager servers, ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, serverId, "plugins")) return Results.Forbid();
    var server = await servers.FindAsync(serverId);
    if (server is null) return Results.NotFound();
    var configFiles = PluginConfigRoots(server).Where(Directory.Exists)
        .SelectMany(root => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        .Where(path => path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)).ToList();
    var plugins = PluginRoots(server).Where(x => Directory.Exists(x.Path)).SelectMany(x =>
        Directory.EnumerateFiles(x.Path, "*", SearchOption.AllDirectories)
        .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase))
        .Select(path =>
        {
            var enabled = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var dllPath = enabled ? path : path[..^".disabled".Length];
            var name = Path.GetFileNameWithoutExtension(dllPath);
            string version;
            try { version = System.Reflection.AssemblyName.GetAssemblyName(path).Version?.ToString() ?? "unknown"; }
            catch { version = "unknown"; }
            var normalizedName = NormalizePluginName(name);
            var configs = configFiles.Where(file =>
                NormalizePluginName(Path.GetFileNameWithoutExtension(file)).Contains(normalizedName)
                || NormalizePluginName(Path.GetDirectoryName(file) ?? "").Contains(normalizedName))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray();
            return new { name, version, framework = x.Framework, enabled, path, configPaths = configs };
        })).ToList();
    return Results.Ok(plugins.DistinctBy(plugin => plugin.path));
});
api.MapPost("/plugins/{serverId:guid}/action", async (
    Guid serverId, PluginActionRequest request, ServerManager servers, AuditService audit, ClaimsPrincipal user, JsonStore store) =>
{
    // Plugin process changes require the dedicated plugin-management permission.
    if (!await Can(user, store, serverId, "plugins.manage"))
        return Results.Forbid();
    var server = await servers.FindAsync(serverId);
    if (server is null) return Results.NotFound();
    var path = EnsurePathInRoots(request.Path, PluginRoots(server).Select(x => x.Path));
    var action = request.Action.Trim().ToLowerInvariant();
    if (action is not ("load" or "unload" or "restart"))
        return Results.BadRequest(new { error = "Action must be load, unload, or restart." });

    if (action == "restart")
    {
        if (!File.Exists(path)) return Results.NotFound();
        await servers.RestartAsync(serverId, Actor(user));
    }
    else
    {
        if (!File.Exists(path)) return Results.NotFound();
        await servers.StopAsync(serverId, Actor(user), force: true);
        try
        {
            if (action == "unload" && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Move(path, path + ".disabled");
            else if (action == "load" && path.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase))
                File.Move(path, path[..^".disabled".Length]);
        }
        finally
        {
            await servers.StartAsync(serverId, Actor(user));
        }
    }
    await audit.AddAsync(Actor(user), $"plugin.{action}", Path.GetFileName(path), "Applied with a clean server restart");
    return Results.Ok(new { restarted = true });
});
api.MapGet("/plugins/{serverId:guid}/config", async (Guid serverId, string path, ServerManager servers, ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, serverId, "config.view")) return Results.Forbid();
    var server = await servers.FindAsync(serverId);
    if (server is null) return Results.NotFound();
    var safePath = EnsurePathInRoots(path, PluginConfigRoots(server));
    return File.Exists(safePath)
        ? Results.Ok(new { path = safePath, content = await File.ReadAllTextAsync(safePath) })
        : Results.NotFound();
});
api.MapPut("/plugins/{serverId:guid}/config", async (
    Guid serverId, PluginConfigRequest request, ServerManager servers, AuditService audit, ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, serverId, "config.write"))
        return Results.Forbid();
    var server = await servers.FindAsync(serverId);
    if (server is null) return Results.NotFound();
    var safePath = EnsurePathInRoots(request.Path, PluginConfigRoots(server));
    if (!File.Exists(safePath)) return Results.NotFound();
    await File.WriteAllTextAsync(safePath, request.Content);
    await audit.AddAsync(Actor(user), "plugin.config", Path.GetFileName(safePath), "Configuration saved");
    return Results.NoContent();
});
api.MapGet("/users", async (JsonStore store) => (await store.ReadAsync<PanelUser>("users")).Select(user => new
{
    user.Id, user.Username, user.Role, user.Enabled, user.CreatedAt,
    serverIds = user.ServerAccess?.Select(x => x.ServerId).Distinct().ToArray() ?? user.ServerIds ?? [],
    permissions = user.Permissions ?? [], serverAccess = user.ServerAccess ?? []
})).RequireAuthorization("Owner");
api.MapPost("/users", async (AccountRequest request, JsonStore store, PasswordService passwords, AuditService audit, ClaimsPrincipal actor) =>
{
    if (string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { error = "A password is required for a new account." });
    var users = await store.ReadAsync<PanelUser>("users");
    if (users.Any(x => x.Username.Equals(request.Username, StringComparison.OrdinalIgnoreCase)))
        return Results.Conflict(new { error = "Username already exists." });
    var user = new PanelUser(Guid.NewGuid(), request.Username.Trim(), passwords.Hash(request.Password),
        "Operator", request.Enabled, DateTimeOffset.UtcNow, request.ServerIds.Distinct().ToArray(),
        request.Permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        request.ServerAccess?.Select(x => new ServerAccessGrant(x.ServerId,
            x.Permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray());
    users.Add(user);
    await store.WriteAsync("users", users);
    await audit.AddAsync(Actor(actor), "user.create", user.Username, user.Role);
    return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Username, user.Role, user.Enabled });
}).RequireAuthorization("Owner");
api.MapPut("/users/{id:guid}", async (
    Guid id, AccountRequest request, JsonStore store, PasswordService passwords,
    AuditService audit, ClaimsPrincipal actor) =>
{
    var users = await store.ReadAsync<PanelUser>("users");
    var index = users.FindIndex(x => x.Id == id);
    if (index < 0) return Results.NotFound();
    if (users[index].Role == "Owner") return Results.BadRequest(new { error = "The owner account cannot be changed here." });
    if (users.Any(x => x.Id != id && x.Username.Equals(request.Username.Trim(), StringComparison.OrdinalIgnoreCase)))
        return Results.Conflict(new { error = "Username already exists." });
    var existing = users[index];
    users[index] = existing with
    {
        Username = request.Username.Trim(),
        PasswordHash = string.IsNullOrWhiteSpace(request.Password) ? existing.PasswordHash : passwords.Hash(request.Password),
        Enabled = request.Enabled,
        ServerIds = request.ServerIds.Distinct().ToArray(),
        Permissions = request.Permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        ServerAccess = request.ServerAccess?.Select(x => new ServerAccessGrant(x.ServerId,
            x.Permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray()
    };
    await store.WriteAsync("users", users);
    await audit.AddAsync(Actor(actor), "user.update", users[index].Username, "Access permissions changed");
    return Results.NoContent();
}).RequireAuthorization("Owner");
api.MapDelete("/users/{id:guid}", async (Guid id, JsonStore store, AuditService audit, ClaimsPrincipal actor) =>
{
    var users = await store.ReadAsync<PanelUser>("users");
    var account = users.FirstOrDefault(x => x.Id == id);
    if (account is null) return Results.NotFound();
    if (account.Role == "Owner") return Results.BadRequest(new { error = "The owner account cannot be deleted." });
    users.Remove(account);
    await store.WriteAsync("users", users);
    await audit.AddAsync(Actor(actor), "user.delete", account.Username, "Account removed");
    return Results.NoContent();
}).RequireAuthorization("Owner");
api.MapPut("/users/me/password", async (LoginRequest request, JsonStore store, PasswordService passwords, AuditService audit, ClaimsPrincipal actor) =>
{
    var id = Guid.Parse(actor.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var users = await store.ReadAsync<PanelUser>("users");
    var index = users.FindIndex(x => x.Id == id);
    if (index < 0) return Results.NotFound();
    users[index] = users[index] with {
        PasswordHash = passwords.Hash(request.Password),
        SessionVersion = users[index].SessionVersion + 1
    };
    await store.WriteAsync("users", users);
    await audit.AddAsync(Actor(actor), "user.password", users[index].Username, "Password changed");
    return Results.NoContent();
});
api.MapGet("/servers/{id:guid}/backups", async (Guid id, OperationsDataService operations, ClaimsPrincipal user, JsonStore store) =>
    !await Can(user, store, id, "maintenance") ? Results.Forbid()
    : Results.Ok(await operations.BackupsAsync(id)));
api.MapPost("/servers/{id:guid}/backups", async (Guid id, MaintenanceService maintenance, ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, id, "maintenance")) return Results.Forbid();
    return Results.Ok(await maintenance.BackupAsync(id, Actor(user)));
});
api.MapGet("/servers/{id:guid}/backups/{fileName}", async (Guid id, string fileName, OperationsDataService operations, ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, id, "maintenance")) return Results.Forbid();
    var path = operations.BackupPath(id, fileName);
    return File.Exists(path) ? Results.File(path, "application/zip", Path.GetFileName(path)) : Results.NotFound();
});
api.MapPost("/servers/{id:guid}/update", async (Guid id, MaintenanceService maintenance, ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, id, "maintenance")) return Results.Forbid();
    return Results.Ok(new { output = await maintenance.UpdateAsync(id, Actor(user)) });
});
api.MapGet("/integrations", (NotificationService notifications) => notifications.ForClientAsync()).RequireAuthorization("Owner");
api.MapPut("/integrations", async (PanelIntegrationSettings request, NotificationService notifications, AuditService audit, ClaimsPrincipal user) =>
{
    await notifications.SaveFromClientAsync(request);
    await audit.AddAsync(Actor(user), "integrations.update", "Discord", "Notification settings changed");
    return Results.NoContent();
}).RequireAuthorization("Owner");
api.MapPost("/integrations/discord/test", async (NotificationService notifications) =>
{
    await notifications.TestAsync();
    return Results.NoContent();
}).RequireAuthorization("Owner");
api.MapGet("/integrations/notifications/history", (int? take, NotificationService notifications) =>
    notifications.HistoryAsync(take ?? 100)).RequireAuthorization("Owner");
api.MapGet("/integrations/discord/bot/status", (DiscordBotService bot) =>
    Results.Ok(bot.Status)).RequireAuthorization("Owner");
api.MapPost("/integrations/discord/bot/reconnect", (DiscordBotService bot) =>
{
    bot.RequestReconnect();
    return Results.Accepted();
}).RequireAuthorization("Owner");

app.MapHub<PanelHub>("/hub/panel");
app.MapFallbackToFile("index.html");

app.Run();
