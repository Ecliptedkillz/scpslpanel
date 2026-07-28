using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
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
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<BridgeStateService>();
builder.Services.AddSingleton<ServerManager>();
builder.Services.AddHostedService<BootstrapService>();
builder.Services.AddHostedService<SchedulerService>();
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
});
builder.Services.AddAuthorization(options =>
    options.AddPolicy("Owner", policy => policy.RequireRole("Owner")));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Panel:AllowedHosts").Get<string[]>() ?? [])
        .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

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
app.UseAuthentication();
app.UseAuthorization();

static string Actor(ClaimsPrincipal user) => user.Identity?.Name ?? "unknown";

app.MapPost("/api/auth/login", async (LoginRequest request, JsonStore store, PasswordService passwords, HttpContext context) =>
{
    var user = (await store.ReadAsync<PanelUser>("users"))
        .FirstOrDefault(x => x.Enabled && x.Username.Equals(request.Username, StringComparison.OrdinalIgnoreCase));
    if (user is null || !passwords.Verify(request.Password, user.PasswordHash)) return Results.Unauthorized();
    var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role) };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new { user.Id, user.Username, user.Role });
});
app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();
app.MapGet("/api/auth/me", (ClaimsPrincipal user) => Results.Ok(new { username = user.Identity!.Name, role = user.FindFirstValue(ClaimTypes.Role) })).RequireAuthorization();
app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", at = DateTimeOffset.UtcNow }));
app.MapPost("/api/bridge/{serverId:guid}/heartbeat", async (
    Guid serverId, BridgeHeartbeat heartbeat, HttpContext context, ServerManager servers,
    BridgeStateService bridgeState, IHubContext<PanelHub> hub) =>
{
    if (!await servers.ValidateBridgeTokenAsync(serverId, context.Request.Headers["X-Bridge-Token"].FirstOrDefault()))
        return Results.Unauthorized();
    bridgeState.Update(serverId, heartbeat);
    await hub.Clients.All.SendAsync("BridgeChanged", serverId);
    return Results.NoContent();
});

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/overview", async (ServerManager servers, AuditService audit) =>
{
    var snapshots = await servers.SnapshotsAsync();
    return new DashboardOverview(snapshots.Count(x => x.State == ServerState.Online), snapshots.Count,
        snapshots.Sum(x => x.Players), snapshots.Sum(x => x.MemoryBytes), snapshots, await audit.RecentAsync(12));
});
api.MapGet("/servers", (ServerManager servers) => servers.SnapshotsAsync());
api.MapGet("/servers/{id:guid}", async (Guid id, ServerManager servers) =>
    await servers.SnapshotAsync(id) is { } value ? Results.Ok(value) : Results.NotFound());
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
api.MapPost("/servers/{id:guid}/start", async (Guid id, ServerManager servers, ClaimsPrincipal user) => { await servers.StartAsync(id, Actor(user)); return Results.Accepted(); });
api.MapPost("/servers/{id:guid}/stop", async (Guid id, ServerManager servers, ClaimsPrincipal user) => { await servers.StopAsync(id, Actor(user)); return Results.Accepted(); });
api.MapPost("/servers/{id:guid}/restart", async (Guid id, ServerManager servers, ClaimsPrincipal user) => { await servers.RestartAsync(id, Actor(user)); return Results.Accepted(); });
api.MapPost("/servers/{id:guid}/kill", async (Guid id, ServerManager servers, ClaimsPrincipal user) => { await servers.StopAsync(id, Actor(user), true); return Results.Accepted(); }).RequireAuthorization("Owner");
api.MapPost("/servers/{id:guid}/command", async (Guid id, CommandRequest request, ServerManager servers, ClaimsPrincipal user) => { await servers.CommandAsync(id, request.Command, Actor(user)); return Results.Accepted(); });
api.MapGet("/servers/{id:guid}/players", (Guid id, BridgeStateService bridge) => Results.Ok(bridge.Get(id)));
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
api.MapPost("/servers/{id:guid}/players/{playerId}/kick", async (Guid id, string playerId, ModerationRequest request, ServerManager servers, ClaimsPrincipal user) =>
{
    await servers.CommandAsync(id, $"kick {playerId} {request.Reason ?? "Removed by panel"}", Actor(user));
    return Results.Accepted();
});
api.MapPost("/servers/{id:guid}/players/{playerId}/ban", async (Guid id, string playerId, ModerationRequest request, ServerManager servers, ClaimsPrincipal user) =>
{
    var duration = Math.Max(1, request.DurationMinutes ?? 60);
    await servers.CommandAsync(id, $"ban {playerId} {duration} {request.Reason ?? "Banned by panel"}", Actor(user));
    return Results.Accepted();
});

api.MapGet("/servers/{id:guid}/files/{**path}", async (Guid id, string path, ServerManager servers, JsonStore store) =>
{
    var server = await servers.FindAsync(id);
    if (server is null) return Results.NotFound();
    var full = store.ResolveSafePath(server.WorkingDirectory, path);
    return File.Exists(full) ? Results.Text(await File.ReadAllTextAsync(full), "text/plain") : Results.NotFound();
});
api.MapPut("/servers/{id:guid}/files/{**path}", async (Guid id, string path, ConfigFileRequest request, ServerManager servers, JsonStore store, AuditService audit, ClaimsPrincipal user) =>
{
    var server = await servers.FindAsync(id);
    if (server is null) return Results.NotFound();
    var full = store.ResolveSafePath(server.WorkingDirectory, path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.WriteAllTextAsync(full, request.Content);
    await audit.AddAsync(Actor(user), "file.write", server.Name, path);
    return Results.NoContent();
}).RequireAuthorization("Owner");

api.MapGet("/bans", (JsonStore store) => store.ReadAsync<BanEntry>("bans"));
api.MapPost("/bans", async (ModerationRequest request, JsonStore store, AuditService audit, ClaimsPrincipal user) =>
{
    var bans = await store.ReadAsync<BanEntry>("bans");
    var entry = new BanEntry(Guid.NewGuid(), request.PlayerId, request.PlayerId, request.Reason ?? "No reason provided",
        Actor(user), DateTimeOffset.UtcNow, request.DurationMinutes is > 0 ? DateTimeOffset.UtcNow.AddMinutes(request.DurationMinutes.Value) : null, false);
    bans.Insert(0, entry);
    await store.WriteAsync("bans", bans);
    await audit.AddAsync(Actor(user), "player.ban", request.PlayerId, entry.Reason);
    return Results.Created($"/api/bans/{entry.Id}", entry);
});
api.MapDelete("/bans/{id:guid}", async (Guid id, JsonStore store, AuditService audit, ClaimsPrincipal user) =>
{
    var bans = await store.ReadAsync<BanEntry>("bans");
    var index = bans.FindIndex(x => x.Id == id);
    if (index < 0) return Results.NotFound();
    bans[index] = bans[index] with { Revoked = true };
    await store.WriteAsync("bans", bans);
    await audit.AddAsync(Actor(user), "player.unban", bans[index].Target, bans[index].Reason);
    return Results.NoContent();
});
api.MapGet("/audit", (int? take, AuditService audit) => audit.RecentAsync(take ?? 100));
api.MapGet("/schedules", (JsonStore store) => store.ReadAsync<ScheduleEntry>("schedules"));
api.MapPost("/schedules", async (ScheduleRequest request, JsonStore store, AuditService audit, ClaimsPrincipal user) =>
{
    var schedules = await store.ReadAsync<ScheduleEntry>("schedules");
    var item = new ScheduleEntry(Guid.NewGuid(), request.ServerId, request.Name, request.Cron, request.Action, request.Enabled, null);
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
api.MapGet("/plugins/{serverId:guid}", async (Guid serverId, ServerManager servers) =>
{
    var server = await servers.FindAsync(serverId);
    if (server is null) return Results.NotFound();
    var labApiRoots = new[]
    {
        Path.Combine(server.WorkingDirectory, "AppData", "SCP Secret Laboratory", "LabAPI", "plugins"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SCP Secret Laboratory", "LabAPI", "plugins")
    };
    var roots = new List<(string Framework, string Path)>
    {
        ("EXILED", Path.Combine(server.WorkingDirectory, "EXILED", "Plugins")),
        ("NWAPI", Path.Combine(server.WorkingDirectory, "PluginAPI", "plugins"))
    };
    roots.AddRange(labApiRoots.Select(path => ("LabAPI", path)));
    var plugins = roots.Where(x => Directory.Exists(x.Path)).SelectMany(x =>
        Directory.EnumerateFiles(x.Path, "*.dll", SearchOption.AllDirectories)
        .Select(path => new PluginEntry(Path.GetFileNameWithoutExtension(path), "unknown", x.Item1, true, path))).ToList();
    return Results.Ok(plugins.DistinctBy(plugin => plugin.Path));
});
api.MapGet("/users", (JsonStore store) => store.ReadAsync<PanelUser>("users")).RequireAuthorization("Owner");
api.MapPost("/users", async (LoginRequest request, JsonStore store, PasswordService passwords, AuditService audit, ClaimsPrincipal actor) =>
{
    var users = await store.ReadAsync<PanelUser>("users");
    if (users.Any(x => x.Username.Equals(request.Username, StringComparison.OrdinalIgnoreCase)))
        return Results.Conflict(new { error = "Username already exists." });
    var user = new PanelUser(Guid.NewGuid(), request.Username.Trim(), passwords.Hash(request.Password),
        "Administrator", true, DateTimeOffset.UtcNow);
    users.Add(user);
    await store.WriteAsync("users", users);
    await audit.AddAsync(Actor(actor), "user.create", user.Username, user.Role);
    return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Username, user.Role, user.Enabled });
}).RequireAuthorization("Owner");
api.MapPut("/users/me/password", async (LoginRequest request, JsonStore store, PasswordService passwords, AuditService audit, ClaimsPrincipal actor) =>
{
    var id = Guid.Parse(actor.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var users = await store.ReadAsync<PanelUser>("users");
    var index = users.FindIndex(x => x.Id == id);
    if (index < 0) return Results.NotFound();
    users[index] = users[index] with { PasswordHash = passwords.Hash(request.Password) };
    await store.WriteAsync("users", users);
    await audit.AddAsync(Actor(actor), "user.password", users[index].Username, "Password changed");
    return Results.NoContent();
});

app.MapHub<PanelHub>("/hub/panel");
app.MapFallbackToFile("index.html");

app.Run();
