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
builder.Services.AddSingleton<OperationTracker>();
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
        var sessionValue = context.Principal?.FindFirstValue("session_id");
        if (!Guid.TryParse(idValue, out var id) || !int.TryParse(versionValue, out var version)
            || !Guid.TryParse(sessionValue, out var sessionId))
        {
            context.RejectPrincipal();
            return;
        }
        var store = context.HttpContext.RequestServices.GetRequiredService<JsonStore>();
        var user = (await store.ReadAsync<PanelUser>("users")).FirstOrDefault(x => x.Id == id);
        var sessions = await store.ReadAsync<PanelSession>("sessions");
        var sessionIndex = sessions.FindIndex(x => x.Id == sessionId && x.UserId == id && !x.Revoked);
        if (user is null || !user.Enabled || user.SessionVersion != version || sessionIndex < 0)
            context.RejectPrincipal();
        else if (DateTimeOffset.UtcNow - sessions[sessionIndex].LastSeenAt > TimeSpan.FromMinutes(5))
        {
            sessions[sessionIndex] = sessions[sessionIndex] with { LastSeenAt = DateTimeOffset.UtcNow };
            await store.WriteAsync("sessions", sessions);
        }
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
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            context.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        else if (context.Context.Request.Path.StartsWithSegments("/assets"))
            context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    }
});
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
    var session = new PanelSession(Guid.NewGuid(), user.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        context.Request.Headers.UserAgent.ToString());
    var sessions = await store.ReadAsync<PanelSession>("sessions");
    sessions.Add(session);
    await store.WriteAsync("sessions", sessions.Where(x => x.CreatedAt > DateTimeOffset.UtcNow.AddDays(-30)).TakeLast(5000));
    var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role), new Claim("session_version", user.SessionVersion.ToString()), new Claim("session_id", session.Id.ToString()) };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new { user.Id, user.Username, user.Role });
}).RequireRateLimiting("login");
app.MapPost("/api/auth/logout", async (HttpContext context, JsonStore store) =>
{
    if (Guid.TryParse(context.User.FindFirstValue("session_id"), out var sessionId))
    {
        var sessions = await store.ReadAsync<PanelSession>("sessions");
        var index = sessions.FindIndex(x => x.Id == sessionId);
        if (index >= 0) { sessions[index] = sessions[index] with { Revoked = true }; await store.WriteAsync("sessions", sessions); }
    }
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
app.MapGet("/api/auth/sessions", async (ClaimsPrincipal principal, JsonStore store) =>
{
    var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var currentId = principal.FindFirstValue("session_id");
    return Results.Ok((await store.ReadAsync<PanelSession>("sessions"))
        .Where(x => x.UserId == userId && !x.Revoked).OrderByDescending(x => x.LastSeenAt)
        .Select(x => new { x.Id, x.CreatedAt, x.LastSeenAt, x.IpAddress, x.UserAgent, current = x.Id.ToString() == currentId }));
}).RequireAuthorization();
app.MapDelete("/api/auth/sessions/{id:guid}", async (Guid id, ClaimsPrincipal principal, JsonStore store, HttpContext context) =>
{
    var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var sessions = await store.ReadAsync<PanelSession>("sessions");
    var index = sessions.FindIndex(x => x.Id == id && x.UserId == userId);
    if (index < 0) return Results.NotFound();
    sessions[index] = sessions[index] with { Revoked = true };
    await store.WriteAsync("sessions", sessions);
    if (principal.FindFirstValue("session_id") == id.ToString())
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
app.MapGet("/api/bridge/{serverId:guid}/ban-check", async (
    Guid serverId, string? userId, string? ipAddress, HttpContext context,
    ServerManager servers, JsonStore store) =>
{
    if (!await servers.ValidateBridgeTokenAsync(serverId, context.Request.Headers["X-Bridge-Token"].FirstOrDefault()))
        return Results.Unauthorized();

    static string NormalizeUserId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Split('@')[0].Trim();

    var normalizedUserId = NormalizeUserId(userId);
    var normalizedIp = ipAddress?.Trim() ?? "";
    var now = DateTimeOffset.UtcNow;
    var match = (await store.ReadAsync<BanEntry>("bans"))
        .Where(entry => !entry.Revoked && (entry.ExpiresAt is null || entry.ExpiresAt > now)
            && (entry.ServerId is null || entry.ServerId == serverId))
        .OrderByDescending(entry => entry.IssuedAt)
        .FirstOrDefault(entry =>
            (!string.IsNullOrWhiteSpace(normalizedUserId)
                && (NormalizeUserId(entry.UserId).Equals(normalizedUserId, StringComparison.OrdinalIgnoreCase)
                    || NormalizeUserId(entry.Target).Equals(normalizedUserId, StringComparison.OrdinalIgnoreCase)))
            || (!string.IsNullOrWhiteSpace(normalizedIp)
                && (string.Equals(entry.IpAddress?.Trim(), normalizedIp, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Target.Trim(), $"ip:{normalizedIp}", StringComparison.OrdinalIgnoreCase))));

    return Results.Ok(match is null
        ? new BridgeBanCheck(false)
        : new BridgeBanCheck(true, match.Reason, match.ExpiresAt));
});
app.MapGet("/api/bridge/{serverId:guid}/game-role", async (
    Guid serverId, string? userId, HttpContext context, ServerManager servers,
    DiscordLinkService discordLinks) =>
{
    if (!await servers.ValidateBridgeTokenAsync(serverId, context.Request.Headers["X-Bridge-Token"].FirstOrDefault()))
        return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(userId)) return Results.Ok(new BridgeGameRoleAssignment(false));
    var server = await servers.FindAsync(serverId);
    return server is null ? Results.NotFound()
        : Results.Ok(await discordLinks.ResolveGameRoleAsync(server, userId));
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
    BridgeCommandService commands, PlayerDataService playerData, JsonStore store,
    AuditService audit, IHubContext<PanelHub> hub) =>
{
    if (!await servers.ValidateBridgeTokenAsync(serverId, context.Request.Headers["X-Bridge-Token"].FirstOrDefault()))
        return Results.Unauthorized();
    await commands.RecordEventAsync(serverId, value);
    if (value.Type is "kick" or "ban" or "oban" or "unban")
    {
        var actor = string.IsNullOrWhiteSpace(value.Actor) ? "In-game Remote Admin" : value.Actor;
        var target = !string.IsNullOrWhiteSpace(value.UserId) ? value.UserId
            : !string.IsNullOrWhiteSpace(value.DisplayName) ? value.DisplayName : value.PlayerId ?? "unknown";
        var reason = string.IsNullOrWhiteSpace(value.Detail) ? "No reason provided" : value.Detail;
        if (value.Type != "unban")
            await playerData.RecordModerationAsync(serverId, target, value.DisplayName ?? target,
                value.Type, reason, actor, value.DurationSeconds is null ? null
                    : Math.Max(1, (int)Math.Ceiling(value.DurationSeconds.Value / 60d)));

        var bans = await store.ReadAsync<BanEntry>("bans");
        if (value.Type is "ban" or "oban")
        {
            var issuedAt = value.At == default ? DateTimeOffset.UtcNow : value.At;
            var duplicate = bans.Any(x => !x.Revoked && x.Target == target && x.Reason == reason
                && issuedAt - x.IssuedAt < TimeSpan.FromSeconds(10));
            if (!duplicate)
            {
                var eventUserId = target.Contains('@') ? target : null;
                var eventIpAddress = System.Net.IPAddress.TryParse(
                    target.StartsWith("ip:", StringComparison.OrdinalIgnoreCase) ? target[3..] : target,
                    out var parsedAddress) ? parsedAddress.ToString() : null;
                bans.Insert(0, new(Guid.NewGuid(), target, value.DisplayName ?? target, reason, actor,
                    issuedAt, value.DurationSeconds is > 0 ? issuedAt.AddSeconds(value.DurationSeconds.Value) : null,
                    false, serverId, eventUserId, eventIpAddress));
                await store.WriteAsync("bans", bans);
            }
        }
        else if (value.Type == "unban")
        {
            static string NormalizeBanTarget(string input) => input.Split('@')[0].Trim();
            var changed = false;
            for (var index = 0; index < bans.Count; index++)
                if (!bans[index].Revoked
                    && NormalizeBanTarget(bans[index].Target).Equals(
                        NormalizeBanTarget(target), StringComparison.OrdinalIgnoreCase))
                {
                    bans[index] = bans[index] with { Revoked = true };
                    changed = true;
                }
            if (changed) await store.WriteAsync("bans", bans);
        }
        await audit.AddAsync(actor, $"player.{value.Type}", target,
            value.DurationSeconds is > 0 ? $"{reason} ({TimeSpan.FromSeconds(value.DurationSeconds.Value)})" : reason);
    }
    else if (value.Type == "role-sync")
    {
        var target = !string.IsNullOrWhiteSpace(value.UserId) ? value.UserId
            : value.DisplayName ?? value.PlayerId ?? "unknown";
        await audit.AddAsync("Discord role sync", "player.role-sync", target,
            value.Detail ?? "Assigned an in-game Remote Admin group");
    }
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
api.MapGet("/staff-dashboard", async (JsonStore store, ServerManager servers, BridgeStateService bridge,
    ClaimsPrincipal user, OperationTracker operations) =>
{
    var definitions = await servers.DefinitionsAsync();
    var visible = new List<ServerDefinition>();
    foreach (var server in definitions)
        if (await Can(user, store, server.Id, "view")) visible.Add(server);
    var ids = visible.Select(x => x.Id).ToHashSet();
    var players = (await store.ReadAsync<PlayerRecord>("players")).Where(x => ids.Contains(x.ServerId)).ToList();
    return Results.Ok(new
    {
        watchlisted = players.Count(x => x.ModerationHistory.Any(m => m.Type == "watchlist")),
        recentModeration = players.SelectMany(player => player.ModerationHistory.Select(item => new
            { player.CurrentName, player.ServerId, item.Type, item.Reason, item.Actor, item.At }))
            .OrderByDescending(x => x.At).Take(8),
        bridgeIssues = visible.Where(x => !bridge.Get(x.Id).Connected)
            .Select(x => new { x.Id, x.Name }),
        failedOperations = (await operations.ListAsync(100)).Count(x => x.Status == "failed"
            && (x.ServerId is null || ids.Contains(x.ServerId.Value)))
    });
});
api.MapGet("/operations", (int? take, OperationTracker operations) =>
    operations.ListAsync(take ?? 100));
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
api.MapPost("/servers/{id:guid}/players/{playerId}/ban", async (Guid id, string playerId, ModerationRequest request, BridgeCommandService commands, BridgeStateService bridge, PlayerDataService playerData, AuditService audit, ClaimsPrincipal user, JsonStore store, CancellationToken cancellationToken) =>
{
    if (!await Can(user, store, id, "players.ban")) return Results.Forbid();
    var duration = Math.Max(1, request.DurationMinutes ?? 60);
    if (!bridge.Get(id).Connected) return Results.Conflict(new { error = "The LabAPI bridge must be connected to verify a ban." });
    var reason = request.Reason ?? "Banned by panel";
    var player = bridge.Get(id).Players.FirstOrDefault(x => x.Id == playerId);
    if (player is null) return Results.NotFound(new { error = "Player is no longer connected." });
    var result = await commands.ExecuteAsync(id, "ban", playerId, reason, duration * 60, cancellationToken: cancellationToken);
    if (!result.Success) return Results.Conflict(new { error = result.Message ?? "The game server rejected the ban." });

    var actor = Actor(user);
    var target = string.IsNullOrWhiteSpace(player.UserId) ? $"ip:{player.IpAddress}" : player.UserId;
    var issuedAt = DateTimeOffset.UtcNow;
    var bans = await store.ReadAsync<BanEntry>("bans");
    bans.Insert(0, new BanEntry(Guid.NewGuid(), target, player.Nickname, reason, actor,
        issuedAt, issuedAt.AddMinutes(duration), false, id,
        string.IsNullOrWhiteSpace(player.UserId) ? null : player.UserId,
        string.IsNullOrWhiteSpace(player.IpAddress) ? null : player.IpAddress));
    await store.WriteAsync("bans", bans);
    await playerData.RecordModerationAsync(id, target, player.Nickname, "ban", reason, actor, duration);
    await audit.AddAsync(actor, "player.ban", target, $"{reason} ({duration} minutes)");
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
api.MapPost("/servers/{id:guid}/player-history/{playerId:guid}/actions/{actionId:guid}/revoke", async (
    Guid id, Guid playerId, Guid actionId, PlayerDataService players, ServerManager servers,
    ClaimsPrincipal user, JsonStore store, AuditService audit) =>
{
    if (!await Can(user, store, id, "players.ban")) return Results.Forbid();
    var player = await players.FindAsync(id, playerId);
    var action = player?.ModerationHistory.FirstOrDefault(entry => entry.Id == actionId);
    if (player is null || action is null) return Results.NotFound();
    if (action.Type is not ("ban" or "oban"))
        return Results.BadRequest(new { error = "Only ban actions can be revoked." });
    if (action.Revoked) return Results.Conflict(new { error = "This ban is already revoked." });

    static string NormalizeTarget(string value) => value.Split('@')[0]
        .Replace("ip:", "", StringComparison.OrdinalIgnoreCase).Trim();
    var playerTargets = new[] { player.UserId, player.LastIpAddress }
        .Where(value => !string.IsNullOrWhiteSpace(value)).Select(NormalizeTarget).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
    var bans = await store.ReadAsync<BanEntry>("bans");
    var matchIndex = bans.Select((ban, index) => new { ban, index })
        .Where(item => !item.ban.Revoked && (item.ban.ServerId is null || item.ban.ServerId == id)
            && item.ban.Reason.Equals(action.Reason, StringComparison.Ordinal)
            && playerTargets.Contains(NormalizeTarget(item.ban.UserId ?? item.ban.Target)))
        .OrderBy(item => Math.Abs((item.ban.IssuedAt - action.At).TotalSeconds))
        .Select(item => item.index).FirstOrDefault(-1);

    var removedLegacyEntries = 0;
    if (matchIndex >= 0)
    {
        var ban = bans[matchIndex];
        var server = await servers.FindAsync(id);
        if (server is not null)
            removedLegacyEntries = await RemoveLegacyGameBanAsync(server, ban, [player]);
        bans[matchIndex] = ban with { Revoked = true };
        await store.WriteAsync("bans", bans);
    }
    var updated = await players.RevokeActionAsync(id, playerId, actionId);
    if (updated is null) return Results.NotFound();
    await audit.AddAsync(Actor(user), "player.unban", player.UserId,
        $"{action.Reason}; removed {removedLegacyEntries} legacy game-ban entries");
    return Results.Ok(updated);
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

static async Task<int> RemoveLegacyGameBanAsync(
    ServerDefinition server, BanEntry ban, IReadOnlyList<PlayerRecord>? playerRecords = null)
{
    static string Normalize(string value) =>
        value.Trim().StartsWith("ip:", StringComparison.OrdinalIgnoreCase)
            ? value.Trim()[3..]
            : value.Split('@')[0].Trim();

    var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var value in new[] { ban.Target, ban.UserId, ban.IpAddress })
        if (!string.IsNullOrWhiteSpace(value))
        {
            identifiers.Add(value.Trim());
            identifiers.Add(Normalize(value));
        }
    var banUserId = Normalize(ban.UserId ?? ban.Target);
    if (!string.IsNullOrWhiteSpace(banUserId) && playerRecords is not null)
        foreach (var player in playerRecords.Where(player =>
            player.ServerId == server.Id
            && Normalize(player.UserId).Equals(banUserId, StringComparison.OrdinalIgnoreCase)))
            if (!string.IsNullOrWhiteSpace(player.LastIpAddress))
            {
                identifiers.Add(player.LastIpAddress.Trim());
                identifiers.Add(Normalize(player.LastIpAddress));
            }

    var removed = 0;
    foreach (var fileName in new[] { "UserIdBans.txt", "IpBans.txt" })
    {
        var path = Path.Combine(ScpConfigRoot(server), fileName);
        if (!File.Exists(path)) continue;
        var lines = await File.ReadAllLinesAsync(path);
        var retained = lines.Where(line =>
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) return true;
            var fields = line.Split(';');
            if (fields.Length < 2) return true;
            var storedTarget = fields[1].Trim();
            var matches = identifiers.Contains(storedTarget) || identifiers.Contains(Normalize(storedTarget));
            // Older panel records may not contain the IP. Native SCP:SL writes matching
            // UserId/IP rows with the same name, reason and issuer, so use that metadata
            // to remove the paired IP row without affecting unrelated bans.
            if (!matches && fileName.Equals("IpBans.txt", StringComparison.OrdinalIgnoreCase)
                && fields.Length >= 5)
                matches = fields[0].Trim().Equals(ban.DisplayName, StringComparison.OrdinalIgnoreCase)
                    && fields[3].Trim().Equals(ban.Reason, StringComparison.Ordinal)
                    && fields[4].Trim().Equals(ban.IssuedBy, StringComparison.OrdinalIgnoreCase);
            if (matches) removed++;
            return !matches;
        }).ToArray();
        if (retained.Length != lines.Length)
            await File.WriteAllLinesAsync(path, retained);
    }
    return removed;
}

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
api.MapDelete("/bans/{id:guid}", async (
    Guid id, JsonStore store, ServerManager servers, AuditService audit, ClaimsPrincipal user) =>
{
    var bans = await store.ReadAsync<BanEntry>("bans");
    var index = bans.FindIndex(x => x.Id == id);
    if (index < 0) return Results.NotFound();
    var ban = bans[index];
    var definitions = await servers.DefinitionsAsync();
    var affectedServers = ban.ServerId is null
        ? definitions
        : definitions.Where(server => server.Id == ban.ServerId).ToList();
    var playerRecords = await store.ReadAsync<PlayerRecord>("players");
    var removedLegacyEntries = 0;
    foreach (var server in affectedServers)
        removedLegacyEntries += await RemoveLegacyGameBanAsync(server, ban, playerRecords);
    bans[index] = bans[index] with { Revoked = true };
    await store.WriteAsync("bans", bans);
    await audit.AddAsync(Actor(user), "player.unban", bans[index].Target,
        $"{bans[index].Reason}; removed {removedLegacyEntries} legacy game-ban entries");
    return Results.Ok(new { revoked = true, removedLegacyEntries });
}).RequireAuthorization("Owner");
api.MapGet("/audit", (int? take, string? query, string? actor, string? action,
    DateTimeOffset? from, DateTimeOffset? to, AuditService audit) =>
    audit.SearchAsync(take ?? 100, query, actor, action, from, to)).RequireAuthorization("Owner");
api.MapGet("/audit/export", async (string? query, string? actor, string? action,
    DateTimeOffset? from, DateTimeOffset? to, AuditService audit) =>
{
    var entries = await audit.SearchAsync(2000, query, actor, action, from, to);
    static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    var lines = new[] { "Timestamp,Actor,Action,Target,Detail" }.Concat(entries.Select(x =>
        $"{x.At:O},{Csv(x.Actor)},{Csv(x.Action)},{Csv(x.Target)},{Csv(x.Detail)}"));
    return Results.File(System.Text.Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, lines)),
        "text/csv", $"scp-control-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
}).RequireAuthorization("Owner");
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
api.MapPost("/servers/{id:guid}/backups/{fileName}/restore", async (
    Guid id, string fileName, MaintenanceService maintenance, ClaimsPrincipal user, JsonStore store) =>
{
    if (!await Can(user, store, id, "maintenance")) return Results.Forbid();
    return Results.Ok(await maintenance.RestoreAsync(id, fileName, Actor(user)));
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
api.MapGet("/integrations/discord/diagnostics", (DiscordBotService bot) => bot.DiagnoseAsync())
    .RequireAuthorization("Owner");
api.MapGet("/integrations/discord/roles", (DiscordLinkService discordLinks) =>
    discordLinks.ListGuildRolesAsync()).RequireAuthorization("Owner");
api.MapGet("/system/versions", (BridgeStateService bridge) => Results.Ok(new
{
    panel = typeof(Program).Assembly.GetName().Version?.ToString() ?? "development",
    bridge = typeof(BridgeStateService).Assembly.GetName().Version?.ToString() ?? "development",
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    serverTime = DateTimeOffset.UtcNow
})).RequireAuthorization("Owner");
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

// Reconcile bans revoked before legacy game-file cleanup was introduced.
// This makes upgrading sufficient even when the UI no longer offers an Unban action.
await using (var reconciliationScope = app.Services.CreateAsyncScope())
{
    var reconciliationStore = reconciliationScope.ServiceProvider.GetRequiredService<JsonStore>();
    var reconciliationServers = reconciliationScope.ServiceProvider.GetRequiredService<ServerManager>();
    var reconciliationLogger = reconciliationScope.ServiceProvider
        .GetRequiredService<ILoggerFactory>().CreateLogger("BanReconciliation");
    try
    {
        var revokedBans = (await reconciliationStore.ReadAsync<BanEntry>("bans"))
            .Where(ban => ban.Revoked).ToList();
        var playerRecords = await reconciliationStore.ReadAsync<PlayerRecord>("players");
        var definitions = await reconciliationServers.DefinitionsAsync();
        var removed = 0;
        foreach (var ban in revokedBans)
            foreach (var server in ban.ServerId is null
                ? definitions
                : definitions.Where(server => server.Id == ban.ServerId))
                removed += await RemoveLegacyGameBanAsync(server, ban, playerRecords);
        if (removed > 0)
            reconciliationLogger.LogInformation(
                "Removed {Count} legacy SCP:SL ban entries for previously revoked panel bans", removed);
    }
    catch (Exception exception)
    {
        reconciliationLogger.LogWarning(exception, "Legacy SCP:SL ban reconciliation failed");
    }
}

app.Run();
