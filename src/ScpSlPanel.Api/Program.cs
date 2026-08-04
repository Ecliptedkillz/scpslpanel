using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.RateLimiting;
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;
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
builder.Services.AddSingleton<DiscordDonorSyncService>();
builder.Services.AddSingleton<PermissionManagementService>();
builder.Services.AddSingleton<OperationsDataService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<DiscordBotService>();
builder.Services.AddSingleton<MaintenanceService>();
builder.Services.AddSingleton<RestartCoordinator>();
builder.Services.AddSingleton<ServerManager>();
builder.Services.AddSingleton<DeploymentHealthService>();
builder.Services.AddSingleton<PanelBackupService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PanelBackupService>());
builder.Services.AddHostedService<BootstrapService>();
builder.Services.AddHostedService<SchedulerService>();
builder.Services.AddHostedService<MonitoringService>();
builder.Services.AddHostedService<DailyReportService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DiscordBotService>());
builder.Services.AddSignalR();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Caddy is the only public-facing server and connects from loopback.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
var authentication = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "scpsl_panel";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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
var discordClientId = builder.Configuration["Panel:DiscordOAuth:ClientId"];
var discordClientSecret = builder.Configuration["Panel:DiscordOAuth:ClientSecret"];
var discordOAuthEnabled = !string.IsNullOrWhiteSpace(discordClientId)
    && !string.IsNullOrWhiteSpace(discordClientSecret);
if (discordOAuthEnabled)
{
    authentication.AddOAuth("Discord", options =>
    {
        options.ClientId = discordClientId!;
        options.ClientSecret = discordClientSecret!;
        options.CallbackPath = "/api/auth/discord/callback";
        options.AuthorizationEndpoint = "https://discord.com/oauth2/authorize";
        options.TokenEndpoint = "https://discord.com/api/oauth2/token";
        options.UserInformationEndpoint = "https://discord.com/api/users/@me";
        options.Scope.Add("identify");
        options.SaveTokens = false;
        options.Events = new OAuthEvents
        {
            OnCreatingTicket = async context =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Authorization = new("Bearer", context.AccessToken);
                using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();
                using var profile = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                var discordId = profile.RootElement.GetProperty("id").GetString()!;
                var discordUsername = profile.RootElement.TryGetProperty("global_name", out var globalName)
                    && globalName.ValueKind != JsonValueKind.Null ? globalName.GetString()
                    : profile.RootElement.GetProperty("username").GetString() ?? "Discord user";
                var avatarHash = profile.RootElement.TryGetProperty("avatar", out var avatarValue)
                    && avatarValue.ValueKind != JsonValueKind.Null ? avatarValue.GetString() : null;
                var discordAvatarUrl = avatarHash is null
                    ? $"https://cdn.discordapp.com/embed/avatars/{(ulong.Parse(discordId) >> 22) % 6}.png"
                    : $"https://cdn.discordapp.com/avatars/{discordId}/{avatarHash}.png?size=128";
                if (builder.Configuration.GetValue("Panel:DiscordOAuth:RequireGuildMembership", false))
                {
                    var integrations = await context.HttpContext.RequestServices.GetRequiredService<NotificationService>().GetAsync();
                    if (string.IsNullOrWhiteSpace(integrations.DiscordGuildId) || string.IsNullOrWhiteSpace(integrations.DiscordBotToken))
                        throw new InvalidOperationException("Discord guild membership enforcement is enabled, but the guild or bot token is not configured.");
                    using var membership = new HttpRequestMessage(HttpMethod.Get,
                        $"https://discord.com/api/v10/guilds/{integrations.DiscordGuildId}/members/{discordId}");
                    membership.Headers.Authorization = new("Bot", integrations.DiscordBotToken);
                    using var membershipResponse = await context.Backchannel.SendAsync(membership, context.HttpContext.RequestAborted);
                    if (!membershipResponse.IsSuccessStatusCode)
                        throw new InvalidOperationException("Your Discord account is not a member of the required server.");
                }
                var store = context.HttpContext.RequestServices.GetRequiredService<JsonStore>();
                var users = await store.ReadAsync<PanelUser>("users", context.HttpContext.RequestAborted);
                PanelUser? user;
                if (context.Properties.Items.TryGetValue("panel_link_user", out var linkUserId)
                    && Guid.TryParse(linkUserId, out var parsedUserId))
                {
                    var index = users.FindIndex(value => value.Id == parsedUserId && value.Enabled);
                    if (index < 0) throw new InvalidOperationException("The panel account is no longer available.");
                    if (users.Any(value => value.Id != parsedUserId && value.DiscordId == discordId))
                        throw new InvalidOperationException("That Discord account is already connected to another panel account.");
                    users[index] = users[index] with { DiscordId = discordId, DiscordUsername = discordUsername,
                        DiscordAvatarUrl = discordAvatarUrl, DiscordLinkedAt = DateTimeOffset.UtcNow };
                    await store.WriteAsync("users", users, context.HttpContext.RequestAborted);
                    user = users[index];
                    context.Properties.RedirectUri = "/?discord=linked";
                    var audit = context.HttpContext.RequestServices.GetRequiredService<AuditService>();
                    await audit.AddAsync(user.Username, "user.discord-link", discordUsername ?? "Discord user", "Discord login connected");
                }
                else
                {
                    user = users.FirstOrDefault(value => value.Enabled && value.DiscordId == discordId);
                    if (user is null) throw new InvalidOperationException("This Discord account is not connected to an enabled panel account.");
                    var audit = context.HttpContext.RequestServices.GetRequiredService<AuditService>();
                    await audit.AddAsync(user.Username, "auth.discord-success", user.Username,
                        $"Discord sign-in from {context.HttpContext.Connection.RemoteIpAddress}");
                }

                var session = new PanelSession(Guid.NewGuid(), user.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    context.HttpContext.Request.Headers.UserAgent.ToString());
                var sessions = await store.ReadAsync<PanelSession>("sessions", context.HttpContext.RequestAborted);
                sessions.Add(session);
                await store.WriteAsync("sessions", sessions.Where(value => value.CreatedAt > DateTimeOffset.UtcNow.AddDays(-30)).TakeLast(5000), context.HttpContext.RequestAborted);
                var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role), new Claim("session_version", user.SessionVersion.ToString()), new Claim("session_id", session.Id.ToString()), new Claim("discord_id", discordId) };
                context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            },
            OnRemoteFailure = async context =>
            {
                var audit = context.HttpContext.RequestServices.GetRequiredService<AuditService>();
                await audit.AddAsync(context.HttpContext.User.Identity?.Name ?? "anonymous", "auth.discord-failure",
                    "Discord OAuth", $"{context.Failure?.Message ?? "Authentication failed"}; IP {context.HttpContext.Connection.RemoteIpAddress}");
                context.HandleResponse();
                context.Response.Redirect("/?discord_error=" + Uri.EscapeDataString(context.Failure?.Message ?? "Discord authentication failed."));
            }
        };
    });
}
builder.Services.AddAuthorization(options =>
    options.AddPolicy("Owner", policy => policy.RequireRole("Owner")));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Panel:AllowedHosts").Get<string[]>() ?? [])
        .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.PermitLimit = 8;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});

var app = builder.Build();
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
    context.Response.Headers[HeaderNames.XFrameOptions] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers[HeaderNames.ContentSecurityPolicy] =
        "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'; " +
        "connect-src 'self' ws: wss:; img-src 'self' data: https:; " +
        "style-src 'self' 'unsafe-inline'; script-src 'self'";
    if (context.Request.IsHttps)
        context.Response.Headers[HeaderNames.StrictTransportSecurity] = "max-age=31536000; includeSubDomains";
    await next();
});
app.Use(async (context, next) =>
{
    var unsafeMethod = !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method)
        && !HttpMethods.IsOptions(context.Request.Method);
    var protectedApi = context.Request.Path.StartsWithSegments("/api")
        && !context.Request.Path.StartsWithSegments("/api/bridge")
        && !context.Request.Path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase);
    if (unsafeMethod && protectedApi
        && !string.Equals(context.Request.Headers["X-Panel-Request"], "1", StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "The request could not be verified. Refresh the panel and try again." });
        return;
    }
    await next();
});
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
        var message = app.Environment.IsDevelopment() ? ex.Message : context.Response.StatusCode switch
        {
            404 => "The requested resource was not found.",
            400 => "The request was invalid.",
            409 => "The request conflicts with the current state.",
            422 => "The requested file could not be processed.",
            _ => "An unexpected server error occurred."
        };
        await context.Response.WriteAsJsonAsync(new { error = message });
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
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var method = context.Request.Method;
    var sensitive =
        (HttpMethods.IsDelete(method) && (path.StartsWithSegments("/api/users") || path.StartsWithSegments("/api/servers")
            || path.Equals("/api/auth/discord/link")))
        || (HttpMethods.IsPut(method) && ((path.StartsWithSegments("/api/users") && !path.StartsWithSegments("/api/users/me")) || path.Equals("/api/integrations")))
        || (HttpMethods.IsPost(method) && (path.Equals("/api/bans") || path.Equals("/api/auth/sessions/revoke")
            || (path.StartsWithSegments("/api/servers") && (path.Value?.EndsWith("/restore", StringComparison.OrdinalIgnoreCase) == true
                || path.Value?.EndsWith("/update", StringComparison.OrdinalIgnoreCase) == true))));
    if (sensitive && context.User.Identity?.IsAuthenticated == true && !RecentlyReauthenticated(context.User))
    {
        await ReauthenticationRequired().ExecuteAsync(context);
        return;
    }
    await next();
});

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

static bool RecentlyReauthenticated(ClaimsPrincipal principal) =>
    long.TryParse(principal.FindFirstValue("reauth_at"), out var value)
    && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(value) < TimeSpan.FromMinutes(5);

static IResult ReauthenticationRequired() => Results.Json(new
{
    error = "Confirm your identity before performing this sensitive action.",
    reauthenticationRequired = true
}, statusCode: 428);

app.MapPost("/api/auth/login", async (LoginRequest request, JsonStore store, PasswordService passwords, TotpService totp, AuditService audit, HttpContext context) =>
{
    var user = (await store.ReadAsync<PanelUser>("users"))
        .FirstOrDefault(x => x.Enabled && x.Username.Equals(request.Username, StringComparison.OrdinalIgnoreCase));
    if (user is null || !passwords.Verify(request.Password, user.PasswordHash))
    {
        await audit.AddAsync("anonymous", "auth.password-failure", request.Username.Trim(),
            $"Rejected password sign-in from {context.Connection.RemoteIpAddress}");
        return Results.Unauthorized();
    }
    if (user.TotpEnabled && (string.IsNullOrWhiteSpace(user.TotpSecret) || !totp.Verify(user.TotpSecret, request.Code)))
    {
        await audit.AddAsync(user.Username, "auth.2fa-failure", user.Username,
            $"Rejected second-factor sign-in from {context.Connection.RemoteIpAddress}");
        return Results.Json(new { error = "A valid two-factor authentication code is required.", requiresTwoFactor = true }, statusCode: 401);
    }
    var session = new PanelSession(Guid.NewGuid(), user.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        context.Request.Headers.UserAgent.ToString());
    var sessions = await store.ReadAsync<PanelSession>("sessions");
    sessions.Add(session);
    await store.WriteAsync("sessions", sessions.Where(x => x.CreatedAt > DateTimeOffset.UtcNow.AddDays(-30)).TakeLast(5000));
    var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role), new Claim("session_version", user.SessionVersion.ToString()), new Claim("session_id", session.Id.ToString()) };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    await audit.AddAsync(user.Username, "auth.password-success", user.Username,
        $"Password sign-in from {context.Connection.RemoteIpAddress}");
    return Results.Ok(new { user.Id, user.Username, user.Role });
}).RequireRateLimiting("login");
app.MapGet("/api/auth/discord/status", (ClaimsPrincipal principal) => Results.Ok(new
{
    enabled = discordOAuthEnabled,
    linked = principal.Identity?.IsAuthenticated == true && principal.HasClaim(claim => claim.Type == "discord_id")
}));
app.MapGet("/api/auth/discord/login", () => discordOAuthEnabled
    ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, ["Discord"])
    : Results.NotFound(new { error = "Discord login is not configured." })).RequireRateLimiting("login");
app.MapGet("/api/auth/discord/link", (ClaimsPrincipal principal) =>
{
    if (!discordOAuthEnabled) return Results.NotFound(new { error = "Discord login is not configured." });
    var properties = new AuthenticationProperties { RedirectUri = "/?discord=linked" };
    properties.Items["panel_link_user"] = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    return Results.Challenge(properties, ["Discord"]);
}).RequireAuthorization();
app.MapPost("/api/auth/reauthenticate", async (ReauthenticationRequest request, ClaimsPrincipal principal,
    HttpContext context, JsonStore store, PasswordService passwords, TotpService totp, AuditService audit) =>
{
    var users = await store.ReadAsync<PanelUser>("users");
    var user = users.FirstOrDefault(value => value.Id.ToString() == principal.FindFirstValue(ClaimTypes.NameIdentifier) && value.Enabled);
    if (user is null) return Results.Unauthorized();
    var passwordValid = !string.IsNullOrWhiteSpace(request.Password) && passwords.Verify(request.Password, user.PasswordHash);
    var totpValid = user.TotpEnabled && !string.IsNullOrWhiteSpace(user.TotpSecret)
        && !string.IsNullOrWhiteSpace(request.Code) && totp.Verify(user.TotpSecret, request.Code);
    if (!passwordValid && !totpValid)
    {
        await audit.AddAsync(user.Username, "auth.reauthentication-failure", user.Username,
            $"Sensitive-action confirmation rejected from {context.Connection.RemoteIpAddress}");
        return Results.Unauthorized();
    }
    var claims = principal.Claims.Where(claim => claim.Type != "reauth_at").Append(
        new Claim("reauth_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    await audit.AddAsync(user.Username, "auth.reauthentication-success", user.Username,
        $"Sensitive-action confirmation from {context.Connection.RemoteIpAddress}");
    return Results.NoContent();
}).RequireAuthorization().RequireRateLimiting("login");
app.MapDelete("/api/auth/discord/link", async (ClaimsPrincipal principal, JsonStore store, AuditService audit) =>
{
    var users = await store.ReadAsync<PanelUser>("users");
    var index = users.FindIndex(value => value.Id.ToString() == principal.FindFirstValue(ClaimTypes.NameIdentifier));
    if (index < 0) return Results.Unauthorized();
    var previous = users[index].DiscordUsername ?? users[index].DiscordId;
    users[index] = users[index] with { DiscordId = null, DiscordUsername = null, DiscordAvatarUrl = null, DiscordLinkedAt = null };
    await store.WriteAsync("users", users);
    await audit.AddAsync(users[index].Username, "user.discord-unlink", previous ?? "Discord", "Discord login disconnected");
    return Results.NoContent();
}).RequireAuthorization();
app.MapPost("/api/auth/logout", async (HttpContext context, JsonStore store, AuditService audit) =>
{
    if (Guid.TryParse(context.User.FindFirstValue("session_id"), out var sessionId))
    {
        var sessions = await store.ReadAsync<PanelSession>("sessions");
        var index = sessions.FindIndex(x => x.Id == sessionId);
        if (index >= 0) { sessions[index] = sessions[index] with { Revoked = true }; await store.WriteAsync("sessions", sessions); }
    }
    await audit.AddAsync(Actor(context.User), "auth.logout", Actor(context.User),
        $"Signed out from {context.Connection.RemoteIpAddress}");
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();
app.MapGet("/api/auth/me", async (ClaimsPrincipal principal, JsonStore store) =>
{
    var user = await CurrentUser(principal, store);
    return user is null ? Results.Unauthorized() : Results.Ok(new
    {
        user.Username, user.Role, serverIds = user.ServerAccess?.Select(x => x.ServerId).Distinct().ToArray() ?? user.ServerIds ?? [],
        permissions = user.Permissions ?? [], serverAccess = user.ServerAccess ?? [], twoFactorEnabled = user.TotpEnabled,
        discordLinked = !string.IsNullOrWhiteSpace(user.DiscordId), discordUsername = user.DiscordUsername,
        discordAvatarUrl = user.DiscordAvatarUrl, discordLinkedAt = user.DiscordLinkedAt
    });
}).RequireAuthorization();
app.MapGet("/api/users/me/preferences", async (ClaimsPrincipal principal, JsonStore store) =>
{
    var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    return (await store.ReadAsync<UserPreference>("user-preferences")).FirstOrDefault(value => value.UserId == userId)
        ?? new UserPreference(userId, [], ["status", "servers", "activity", "staff", "permissions"]);
}).RequireAuthorization();
app.MapPut("/api/users/me/preferences", async (UserPreferenceRequest request, ClaimsPrincipal principal, JsonStore store) =>
{
    var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var values = await store.ReadAsync<UserPreference>("user-preferences");
    var preference = new UserPreference(userId, request.FavoriteServerIds.Distinct().ToArray(),
        request.DashboardWidgets.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray(), request.NotificationsReadAt);
    var index = values.FindIndex(value => value.UserId == userId);
    if (index < 0) values.Add(preference); else values[index] = preference;
    await store.WriteAsync("user-preferences", values);
    return Results.NoContent();
}).RequireAuthorization();
app.MapGet("/api/users/me/inbox", async (ClaimsPrincipal principal, JsonStore store) =>
{
    var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var preference = (await store.ReadAsync<UserPreference>("user-preferences")).FirstOrDefault(value => value.UserId == userId);
    var username = principal.Identity?.Name ?? "";
    var owner = principal.IsInRole("Owner");
    var entries = (await store.ReadAsync<AuditEntry>("audit")).Where(value => owner || value.Actor == username)
        .OrderByDescending(value => value.At).Take(50).Select(value => new
        {
            id = value.Id, at = value.At, title = value.Action, detail = $"{value.Target}: {value.Detail}",
            unread = preference?.NotificationsReadAt is null || value.At > preference.NotificationsReadAt
        });
    return Results.Ok(entries);
}).RequireAuthorization();
app.MapPost("/api/users/me/inbox/read", async (ClaimsPrincipal principal, JsonStore store) =>
{
    var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var values = await store.ReadAsync<UserPreference>("user-preferences");
    var index = values.FindIndex(value => value.UserId == userId);
    if (index < 0) values.Add(new(userId, [], ["status", "servers", "activity", "staff", "permissions"], DateTimeOffset.UtcNow));
    else values[index] = values[index] with { NotificationsReadAt = DateTimeOffset.UtcNow };
    await store.WriteAsync("user-preferences", values);
    return Results.NoContent();
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
app.MapPost("/api/auth/2fa/confirm", async (TotpRequest request, ClaimsPrincipal principal, JsonStore store, TotpService totp, AuditService audit) =>
{
    var users = await store.ReadAsync<PanelUser>("users");
    var index = users.FindIndex(x => x.Id.ToString() == principal.FindFirstValue(ClaimTypes.NameIdentifier));
    if (index < 0 || string.IsNullOrWhiteSpace(users[index].TotpSecret) || !totp.Verify(users[index].TotpSecret!, request.Code))
        return Results.BadRequest(new { error = "The verification code is invalid." });
    users[index] = users[index] with { TotpEnabled = true };
    await store.WriteAsync("users", users);
    await audit.AddAsync(Actor(principal), "auth.2fa-enabled", users[index].Username, "TOTP enabled");
    return Results.NoContent();
}).RequireAuthorization();
app.MapPost("/api/auth/2fa/disable", async (TotpRequest request, ClaimsPrincipal principal, JsonStore store, TotpService totp, AuditService audit) =>
{
    var users = await store.ReadAsync<PanelUser>("users");
    var index = users.FindIndex(x => x.Id.ToString() == principal.FindFirstValue(ClaimTypes.NameIdentifier));
    if (index < 0 || !users[index].TotpEnabled || !totp.Verify(users[index].TotpSecret ?? "", request.Code))
        return Results.BadRequest(new { error = "The verification code is invalid." });
    users[index] = users[index] with { TotpEnabled = false, TotpSecret = null };
    await store.WriteAsync("users", users);
    await audit.AddAsync(Actor(principal), "auth.2fa-disabled", users[index].Username, "TOTP disabled");
    return Results.NoContent();
}).RequireAuthorization();
app.MapPost("/api/auth/sessions/revoke", async (ClaimsPrincipal principal, JsonStore store, HttpContext context, AuditService audit) =>
{
    var users = await store.ReadAsync<PanelUser>("users");
    var index = users.FindIndex(x => x.Id.ToString() == principal.FindFirstValue(ClaimTypes.NameIdentifier));
    if (index < 0) return Results.Unauthorized();
    users[index] = users[index] with { SessionVersion = users[index].SessionVersion + 1 };
    await store.WriteAsync("users", users);
    await audit.AddAsync(Actor(principal), "auth.sessions-revoke-all", users[index].Username, "All active sessions revoked");
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
app.MapDelete("/api/auth/sessions/{id:guid}", async (Guid id, ClaimsPrincipal principal, JsonStore store, HttpContext context, AuditService audit) =>
{
    var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var sessions = await store.ReadAsync<PanelSession>("sessions");
    var index = sessions.FindIndex(x => x.Id == id && x.UserId == userId);
    if (index < 0) return Results.NotFound();
    sessions[index] = sessions[index] with { Revoked = true };
    await store.WriteAsync("sessions", sessions);
    await audit.AddAsync(Actor(principal), "auth.session-revoke", id.ToString(), sessions[index].IpAddress);
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
app.MapGet("/api/bridge/{serverId:guid}/custom-badge", async (
    Guid serverId, string? userId, HttpContext context, ServerManager servers,
    DiscordLinkService discordLinks) =>
{
    if (!await servers.ValidateBridgeTokenAsync(serverId, context.Request.Headers["X-Bridge-Token"].FirstOrDefault()))
        return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(userId)) return Results.Ok(new BridgeCustomBadge(false));
    var server = await servers.FindAsync(serverId);
    return server is null ? Results.NotFound()
        : Results.Ok(await discordLinks.ResolveCustomBadgeAsync(server, userId));
});
app.MapGet("/api/bridge/{serverId:guid}/tag-options", async (
    Guid serverId, string? userId, HttpContext context, ServerManager servers,
    DiscordLinkService discordLinks, JsonStore store) =>
{
    if (!await servers.ValidateBridgeTokenAsync(serverId, context.Request.Headers["X-Bridge-Token"].FirstOrDefault()))
        return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(userId)) return Results.Ok(new BridgeTagOptions([]));
    var server = await servers.FindAsync(serverId);
    if (server is null) return Results.NotFound();
    var steamId = userId.Split('@', 2)[0].Trim();
    var options = await discordLinks.ResolveTagOptionsAsync(server, userId);
    var selected = (await store.ReadAsync<PlayerTagPreference>("tag-preferences"))
        .LastOrDefault(value => value.ServerId == serverId
            && value.SteamId.Equals(steamId, StringComparison.OrdinalIgnoreCase))?.SelectedId;
    return Results.Ok(options with { SelectedId = selected });
});
app.MapPut("/api/bridge/{serverId:guid}/tag-preference", async (
    Guid serverId, string? userId, BridgeTagPreference value, HttpContext context,
    ServerManager servers, JsonStore store) =>
{
    if (!await servers.ValidateBridgeTokenAsync(serverId, context.Request.Headers["X-Bridge-Token"].FirstOrDefault()))
        return Results.Unauthorized();
    var steamId = userId?.Split('@', 2)[0].Trim() ?? "";
    if (string.IsNullOrWhiteSpace(steamId)) return Results.BadRequest();
    var preferences = await store.ReadAsync<PlayerTagPreference>("tag-preferences");
    preferences.RemoveAll(item => item.ServerId == serverId
        && item.SteamId.Equals(steamId, StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(value.SelectedId))
        preferences.Add(new(serverId, steamId, value.SelectedId.Trim(), DateTimeOffset.UtcNow));
    await store.WriteAsync("tag-preferences", preferences);
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
    BridgeCommandService commands, PlayerDataService playerData, JsonStore store,
    AuditService audit, IHubContext<PanelHub> hub) =>
{
    if (!await servers.ValidateBridgeTokenAsync(serverId, context.Request.Headers["X-Bridge-Token"].FirstOrDefault()))
        return Results.Unauthorized();
    await commands.RecordEventAsync(serverId, value);
    if (value.Type == "report")
    {
        var ticket = new ReportTicket(Guid.NewGuid(), serverId,
            value.At == default ? DateTimeOffset.UtcNow : value.At, "open",
            value.UserId ?? "", value.DisplayName ?? "Unknown reporter",
            value.TargetUserId ?? "", value.TargetDisplayName ?? "Unknown player",
            string.IsNullOrWhiteSpace(value.Detail) ? "No reason provided" : value.Detail.Trim());
        var reports = await store.ReadAsync<ReportTicket>("report-tickets");
        reports.Insert(0, ticket);
        await store.WriteAsync("report-tickets", reports.Take(5000));
        var server = await servers.FindAsync(serverId);
        var notifications = app.Services.GetRequiredService<NotificationService>();
        await notifications.SendAsync($"New in-game report · {server?.Name ?? "Unknown server"}",
            $"**Reporter:** {ticket.ReporterName} (`{ticket.ReporterUserId}`)\n"
            + $"**Reported player:** {ticket.TargetName} (`{ticket.TargetUserId}`)\n"
            + $"**Reason:** {ticket.Reason}\n**Ticket:** `{ticket.Id}`",
            "warning", "reports");
        await audit.AddAsync("Built-in report system", "report.created",
            ticket.TargetUserId, $"{ticket.ReporterName}: {ticket.Reason}");
    }
    else if (value.Type is "kick" or "ban" or "oban" or "unban")
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
    else if (value.Type == "permission-denied")
    {
        var target = !string.IsNullOrWhiteSpace(value.UserId) ? value.UserId
            : value.DisplayName ?? value.PlayerId ?? "unknown";
        await audit.AddAsync("Runtime permission provider", "player.permission-denied", target,
            $"Denied custom permission '{value.Detail ?? "unknown"}'.");
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
api.MapGet("/search", async (string? q, JsonStore store, ServerManager servers, ClaimsPrincipal principal) =>
{
    var query = q?.Trim();
    if (string.IsNullOrWhiteSpace(query) || query.Length < 2) return Results.Ok(Array.Empty<object>());
    query = query[..Math.Min(query.Length, 100)];
    var account = await CurrentUser(principal, store);
    if (account is null) return Results.Unauthorized();
    var definitions = await servers.DefinitionsAsync();
    var visible = definitions.Where(server => account.Role == "Owner"
        || account.ServerAccess?.Any(grant => grant.ServerId == server.Id) == true
        || account.ServerIds?.Contains(server.Id) == true).ToList();
    var visibleIds = visible.Select(server => server.Id).ToHashSet();
    var playerVisibleIds = new HashSet<Guid>();
    foreach (var serverId in visibleIds)
        if (await Can(principal, store, serverId, "players.history")) playerVisibleIds.Add(serverId);
    var names = visible.ToDictionary(server => server.Id, server => server.Name);
    var results = new List<object>();
    results.AddRange(visible.Where(server => server.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        .Take(5).Select(server => new { type = "server", title = server.Name,
            subtitle = $"Server · Port {server.QueryPort}", serverId = (Guid?)server.Id, playerId = (Guid?)null }));
    var players = await store.ReadAsync<PlayerRecord>("players");
    results.AddRange(players.Where(player => playerVisibleIds.Contains(player.ServerId) &&
            (player.CurrentName.Contains(query, StringComparison.OrdinalIgnoreCase)
             || player.UserId.Contains(query, StringComparison.OrdinalIgnoreCase)
             || (player.DiscordId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
             || player.NameHistory.Any(name => name.Name.Contains(query, StringComparison.OrdinalIgnoreCase))))
        .OrderByDescending(player => player.LastConnectedAt).Take(12)
        .Select(player => new { type = "player", title = player.CurrentName,
            subtitle = $"{names.GetValueOrDefault(player.ServerId, "Server")} · {player.UserId}",
            serverId = (Guid?)player.ServerId, playerId = (Guid?)player.Id }));
    if (account.Role == "Owner")
    {
        var audit = await store.ReadAsync<AuditEntry>("audit");
        results.AddRange(audit.Where(entry => entry.Actor.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Action.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Target.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Detail.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.At).Take(8)
            .Select(entry => new { type = "audit", title = entry.Action,
                subtitle = $"{entry.Actor} · {entry.Detail}", serverId = (Guid?)null, playerId = (Guid?)null }));
    }
    return Results.Ok(results.Take(25));
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
api.MapGet("/incidents", async (JsonStore store, ClaimsPrincipal user) =>
{
    var incidents = await store.ReadAsync<ManagedIncident>("managed-incidents");
    if (user.IsInRole("Owner")) return Results.Ok(incidents.OrderByDescending(x => x.UpdatedAt));
    var visible = new List<ManagedIncident>();
    foreach (var incident in incidents)
        if (await Can(user, store, incident.ServerId, "monitoring")) visible.Add(incident);
    return Results.Ok(visible.OrderByDescending(x => x.UpdatedAt));
});
api.MapPost("/incidents", async (IncidentCreateRequest request, JsonStore store, AuditService audit, ClaimsPrincipal user) =>
{
    if (!await Can(user, store, request.ServerId, "players.actions")) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Description))
        return Results.BadRequest(new { error = "An incident title and description are required." });
    var severity = request.Severity.Trim().ToLowerInvariant();
    if (severity is not ("low" or "medium" or "high" or "critical"))
        return Results.BadRequest(new { error = "Invalid incident severity." });
    var now = DateTimeOffset.UtcNow;
    var incident = new ManagedIncident(Guid.NewGuid(), request.ServerId, request.Title.Trim(),
        string.IsNullOrWhiteSpace(request.Category) ? "operations" : request.Category.Trim().ToLowerInvariant(),
        severity, "open", request.Description.Trim(), Actor(user), now, now, Notes: []);
    var incidents = await store.ReadAsync<ManagedIncident>("managed-incidents");
    incidents.Insert(0, incident);
    await store.WriteAsync("managed-incidents", incidents.Take(5000));
    await audit.AddAsync(Actor(user), "incident.create", incident.Id.ToString(), incident.Title);
    return Results.Created($"/api/incidents/{incident.Id}", incident);
});
api.MapPut("/incidents/{id:guid}", async (Guid id, IncidentUpdateRequest request, JsonStore store, AuditService audit, ClaimsPrincipal user) =>
{
    var incidents = await store.ReadAsync<ManagedIncident>("managed-incidents");
    var index = incidents.FindIndex(x => x.Id == id);
    if (index < 0) return Results.NotFound();
    if (!await Can(user, store, incidents[index].ServerId, "players.actions")) return Results.Forbid();
    var status = request.Status.Trim().ToLowerInvariant();
    var severity = request.Severity.Trim().ToLowerInvariant();
    if (status is not ("open" or "investigating" or "resolved" or "dismissed")
        || severity is not ("low" or "medium" or "high" or "critical"))
        return Results.BadRequest(new { error = "Invalid incident status or severity." });
    var current = incidents[index];
    incidents[index] = current with { Status = status, Severity = severity,
        AssignedTo = string.IsNullOrWhiteSpace(request.AssignedTo) ? null : request.AssignedTo.Trim(),
        Resolution = request.Resolution?.Trim(), UpdatedAt = DateTimeOffset.UtcNow,
        ResolvedAt = status is "resolved" or "dismissed" ? DateTimeOffset.UtcNow : null };
    await store.WriteAsync("managed-incidents", incidents);
    await audit.AddAsync(Actor(user), $"incident.{status}", id.ToString(), request.Resolution ?? current.Title);
    return Results.Ok(incidents[index]);
});
api.MapPost("/incidents/{id:guid}/notes", async (Guid id, IncidentNoteRequest request, JsonStore store, AuditService audit, ClaimsPrincipal user) =>
{
    if (string.IsNullOrWhiteSpace(request.Text)) return Results.BadRequest(new { error = "A note is required." });
    var incidents = await store.ReadAsync<ManagedIncident>("managed-incidents");
    var index = incidents.FindIndex(x => x.Id == id);
    if (index < 0) return Results.NotFound();
    if (!await Can(user, store, incidents[index].ServerId, "players.actions")) return Results.Forbid();
    var notes = incidents[index].Notes?.ToList() ?? [];
    notes.Add(new IncidentNote(Guid.NewGuid(), request.Text.Trim(), Actor(user), DateTimeOffset.UtcNow));
    incidents[index] = incidents[index] with { Notes = notes, UpdatedAt = DateTimeOffset.UtcNow };
    await store.WriteAsync("managed-incidents", incidents);
    await audit.AddAsync(Actor(user), "incident.note", id.ToString(), request.Text.Trim());
    return Results.Ok(incidents[index]);
});
api.MapGet("/reports", async (JsonStore store, ServerManager servers, ClaimsPrincipal user) =>
{
    var reports = await store.ReadAsync<ReportTicket>("report-tickets");
    if (user.IsInRole("Owner")) return Results.Ok(reports.OrderByDescending(x => x.CreatedAt));
    var visible = new List<ReportTicket>();
    foreach (var report in reports)
        if (await Can(user, store, report.ServerId, "players.history")) visible.Add(report);
    return Results.Ok(visible.OrderByDescending(x => x.CreatedAt));
});
api.MapPut("/reports/{id:guid}", async (
    Guid id, ReportTicketUpdate request, JsonStore store, AuditService audit, ClaimsPrincipal user) =>
{
    var reports = await store.ReadAsync<ReportTicket>("report-tickets");
    var index = reports.FindIndex(x => x.Id == id);
    if (index < 0) return Results.NotFound();
    if (!await Can(user, store, reports[index].ServerId, "players.actions")) return Results.Forbid();
    var status = request.Status.Trim().ToLowerInvariant();
    if (status is not ("open" or "claimed" or "resolved" or "dismissed"))
        return Results.BadRequest(new { error = "Invalid report status." });
    if (status is "resolved" or "dismissed" && string.IsNullOrWhiteSpace(request.Resolution))
        return Results.BadRequest(new { error = "An investigation outcome is required to close a report." });
    reports[index] = reports[index] with
    {
        Status = status,
        AssignedTo = status == "open" ? null : Actor(user),
        Resolution = status == "open" ? null : request.Resolution?.Trim(),
        UpdatedAt = DateTimeOffset.UtcNow
    };
    await store.WriteAsync("report-tickets", reports);
    await audit.AddAsync(Actor(user), $"report.{status}", id.ToString(),
        request.Resolution ?? reports[index].Reason);
    return Results.Ok(reports[index]);
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
api.MapPost("/servers/{id:guid}/kill", async (Guid id, ServerManager servers, AuditService audit, ClaimsPrincipal user) =>
{
    await servers.StopAsync(id, Actor(user), true);
    await audit.AddAsync(Actor(user), "server.kill", id.ToString(), "Force-terminated server process tree");
    return Results.Accepted();
}).RequireAuthorization("Owner");
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
api.MapPost("/servers/{id:guid}/announcement", async (Guid id, AnnouncementRequest request, BridgeCommandService commands, BridgeStateService bridge, AuditService audit, ClaimsPrincipal user, JsonStore store, CancellationToken cancellationToken) =>
{
    if (!await Can(user, store, id, "announcements")) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(request.Message)) return Results.BadRequest(new { error = "Announcement text is required." });
    if (!bridge.Get(id).Connected) return Results.Conflict(new { error = "The LabAPI bridge is not connected." });
    var result = await commands.ExecuteAsync(id, "announcement", message: request.Message.Trim(),
        durationSeconds: Math.Clamp(request.DurationSeconds, 1, ushort.MaxValue), cancellationToken: cancellationToken);
    if (!result.Success) return Results.Conflict(new { error = result.Message });
    await audit.AddAsync(Actor(user), "round.announcement", id.ToString(), request.Message.Trim());
    return Results.Ok(result);
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
        if (!await Can(user, store, server.Id, "players.history")
            && !await Can(user, store, server.Id, "badges.manage")) continue;
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
    permissions = user.Permissions ?? [], serverAccess = user.ServerAccess ?? [],
    discordLinked = !string.IsNullOrWhiteSpace(user.DiscordId), user.DiscordUsername
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
api.MapGet("/integrations/donors-badges", async (
    NotificationService notifications, ServerManager servers, ClaimsPrincipal user, JsonStore store) =>
{
    var settings = await notifications.GetAsync();
    var donorServers = new HashSet<Guid>();
    var badgeServers = new HashSet<Guid>();
    foreach (var server in await servers.DefinitionsAsync())
    {
        if (await Can(user, store, server.Id, "donors.manage")) donorServers.Add(server.Id);
        if (await Can(user, store, server.Id, "badges.manage")) badgeServers.Add(server.Id);
    }
    if (donorServers.Count == 0 && badgeServers.Count == 0) return Results.Forbid();
    return Results.Ok(new DonorBadgeSettings(
        (settings.DiscordDonorRoleGrants ?? []).Where(x => donorServers.Contains(x.ServerId)).ToArray(),
        (settings.CustomUserBadges ?? []).Where(x => badgeServers.Contains(x.ServerId)).ToArray(),
        (settings.CustomRoleBadges ?? []).Where(x => badgeServers.Contains(x.ServerId)).ToArray()));
});
api.MapPut("/integrations/donors-badges", async (
    DonorBadgeSettings request, NotificationService notifications, ServerManager servers,
    BridgeStateService bridge, BridgeCommandService commands, AuditService audit,
    ClaimsPrincipal user, JsonStore store) =>
{
    var donorServers = new HashSet<Guid>();
    var badgeServers = new HashSet<Guid>();
    foreach (var server in await servers.DefinitionsAsync())
    {
        if (await Can(user, store, server.Id, "donors.manage")) donorServers.Add(server.Id);
        if (await Can(user, store, server.Id, "badges.manage")) badgeServers.Add(server.Id);
    }
    if (request.DiscordDonorRoleGrants.Any(x => !donorServers.Contains(x.ServerId))
        || request.CustomUserBadges.Any(x => !badgeServers.Contains(x.ServerId))
        || request.CustomRoleBadges.Any(x => !badgeServers.Contains(x.ServerId)))
        return Results.Forbid();

    var existing = await notifications.GetAsync();
    var donors = (existing.DiscordDonorRoleGrants ?? []).Where(x => !donorServers.Contains(x.ServerId))
        .Concat(request.DiscordDonorRoleGrants).ToArray();
    var userBadges = (existing.CustomUserBadges ?? []).Where(x => !badgeServers.Contains(x.ServerId))
        .Concat(request.CustomUserBadges).ToArray();
    var roleBadges = (existing.CustomRoleBadges ?? []).Where(x => !badgeServers.Contains(x.ServerId))
        .Concat(request.CustomRoleBadges).ToArray();
    await notifications.SaveAsync(existing with
    {
        DiscordDonorRoleGrants = donors,
        CustomUserBadges = userBadges,
        CustomRoleBadges = roleBadges
    });
    await audit.AddAsync(Actor(user), "donors-badges.update", "Discord donor integration",
        $"Saved {request.DiscordDonorRoleGrants.Count} donor mapping(s), "
        + $"{request.CustomRoleBadges.Count} role badge(s), and {request.CustomUserBadges.Count} user badge(s).");
    foreach (var serverId in request.CustomUserBadges.Select(x => x.ServerId)
        .Concat(request.CustomRoleBadges.Select(x => x.ServerId)).Distinct())
        if (bridge.Get(serverId).Connected)
            foreach (var player in bridge.Get(serverId).Players)
                _ = Task.Run(() => commands.ExecuteAsync(serverId, "role-sync", player.Id));
    return Results.NoContent();
});
api.MapPut("/integrations", async (PanelIntegrationSettings request, NotificationService notifications,
    PermissionManagementService permissionManagement, BridgeStateService bridge,
    BridgeCommandService commands, AuditService audit, ClaimsPrincipal user) =>
{
    var before = await notifications.GetAsync();
    var definitions = await app.Services.GetRequiredService<ServerManager>().DefinitionsAsync();
    var issues = permissionManagement.Validate(request.DiscordGameRoleGrants?.ToArray() ?? [], definitions);
    if (issues.Any(issue => issue.Severity == "error"))
        return Results.BadRequest(new { error = "Permission configuration contains validation errors.", issues });
    await notifications.SaveFromClientAsync(request);
    var oldRoles = before.DiscordGameRoleGrants?.Count ?? 0;
    var newRoles = request.DiscordGameRoleGrants?.Count ?? 0;
    var beforeNames = (before.DiscordGameRoleGrants ?? []).Select(role =>
        $"{role.ServerId}:{role.GroupName}").ToHashSet(StringComparer.OrdinalIgnoreCase);
    var afterNames = (request.DiscordGameRoleGrants ?? []).Select(role =>
        $"{role.ServerId}:{role.GroupName}").ToHashSet(StringComparer.OrdinalIgnoreCase);
    var added = afterNames.Except(beforeNames, StringComparer.OrdinalIgnoreCase).Select(value => value.Split(':', 2)[1]);
    var removed = beforeNames.Except(afterNames, StringComparer.OrdinalIgnoreCase).Select(value => value.Split(':', 2)[1]);
    var addedValues = added.ToArray();
    var removedValues = removed.ToArray();
    var auditAction = removedValues.Length > 0 && addedValues.Length == 0 ? "permissions.role-delete"
        : addedValues.Length > 0 && removedValues.Length == 0 ? "permissions.role-create"
        : "permissions.role-update";
    await audit.AddAsync(Actor(user), auditAction, "In-game roles",
        $"Runtime roles changed: {oldRoles} → {newRoles}. Added: {string.Join(", ", addedValues.DefaultIfEmpty("none"))}. "
        + $"Removed: {string.Join(", ", removedValues.DefaultIfEmpty("none"))}. "
        + $"Validation warnings: {issues.Count(issue => issue.Severity != "error")}.");
    foreach (var serverId in (request.DiscordGameRoleGrants ?? []).Select(role => role.ServerId)
        .Concat((request.CustomUserBadges ?? []).Select(badge => badge.ServerId))
        .Concat((request.CustomRoleBadges ?? []).Select(badge => badge.ServerId)).Distinct())
        if (bridge.Get(serverId).Connected)
            foreach (var player in bridge.Get(serverId).Players)
                _ = Task.Run(() => commands.ExecuteAsync(serverId, "role-sync", player.Id));
    return Results.Ok(new { issues });
}).RequireAuthorization("Owner");
api.MapPost("/integrations/discord/test", async (NotificationService notifications) =>
{
    await notifications.TestAsync();
    return Results.NoContent();
}).RequireAuthorization("Owner");
api.MapPost("/integrations/discord/donors/sync", async (
    DiscordBotService bot, AuditService audit, NotificationService notifications,
    ClaimsPrincipal user, JsonStore store) =>
{
    var settings = await notifications.GetAsync();
    foreach (var serverId in (settings.DiscordDonorRoleGrants ?? [])
        .Where(x => x.Enabled).Select(x => x.ServerId).Distinct())
        if (!await Can(user, store, serverId, "donors.manage")) return Results.Forbid();
    var results = await bot.SyncDonorsAsync();
    await audit.AddAsync(Actor(user), "discord.donor-sync", "Donators.csv",
        $"Synchronized {results.Sum(x => x.Donors)} donor rows across {results.Count} server(s).");
    return Results.Ok(results);
});
api.MapGet("/integrations/discord/diagnostics", (DiscordBotService bot) => bot.DiagnoseAsync())
    .RequireAuthorization("Owner");
api.MapGet("/integrations/discord/roles", async (
    DiscordLinkService discordLinks, ServerManager servers, ClaimsPrincipal user, JsonStore store) =>
{
    foreach (var server in await servers.DefinitionsAsync())
        if (await Can(user, store, server.Id, "donors.manage")
            || await Can(user, store, server.Id, "badges.manage"))
            return Results.Ok(await discordLinks.ListGuildRolesAsync());
    return Results.Forbid();
});
api.MapGet("/permissions/health", (PermissionManagementService permissions) =>
    permissions.HealthAsync()).RequireAuthorization("Owner");
api.MapGet("/permissions/diagnose/{serverId:guid}", async (
    Guid serverId, string userId, PermissionManagementService permissions) =>
    await permissions.DiagnoseAsync(serverId, userId) is { } result
        ? Results.Ok(result) : Results.NotFound()).RequireAuthorization("Owner");
api.MapGet("/permissions/native/{serverId:guid}", async (
    Guid serverId, PermissionManagementService permissions) =>
    await permissions.CompareNativeAsync(serverId) is { } result
        ? Results.Ok(result) : Results.NotFound()).RequireAuthorization("Owner");
api.MapPost("/permissions/sync/{serverId:guid}", async (
    Guid serverId, string? playerId, BridgeStateService bridge, BridgeCommandService commands,
    AuditService audit, ClaimsPrincipal user) =>
{
    var status = bridge.Get(serverId);
    if (!status.Connected) return Results.Conflict(new { error = "The bridge is offline." });
    var targets = string.IsNullOrWhiteSpace(playerId) ? status.Players
        : status.Players.Where(player => player.Id == playerId || player.UserId == playerId).ToArray();
    if (targets.Count == 0) return Results.NotFound(new { error = "No matching online player." });
    var results = new List<object>();
    foreach (var player in targets)
    {
        var result = await commands.ExecuteAsync(serverId, "role-sync", player.Id);
        results.Add(new { player.Id, player.Nickname, result.Success, result.Message });
    }
    await audit.AddAsync(Actor(user), "permissions.sync", serverId.ToString(),
        $"Requested live role synchronization for {targets.Count} player(s).");
    return Results.Ok(results);
}).RequireAuthorization("Owner");
api.MapGet("/system/versions", (BridgeStateService bridge) => Results.Ok(new
{
    panel = typeof(Program).Assembly.GetName().Version?.ToString() ?? "development",
    bridge = typeof(BridgeStateService).Assembly.GetName().Version?.ToString() ?? "development",
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    serverTime = DateTimeOffset.UtcNow
})).RequireAuthorization("Owner");
api.MapGet("/system/health", (DeploymentHealthService health) => health.CheckAsync())
    .RequireAuthorization("Owner");
api.MapGet("/system/onboarding", async (DeploymentHealthService health, ServerManager servers, JsonStore store) =>
{
    var report = await health.CheckAsync();
    var definitions = await servers.DefinitionsAsync();
    return Results.Ok(new
    {
        complete = definitions.Count > 0 && report.Checks.All(value => value.Status != "critical")
            && report.Checks.Any(value => value.Key.StartsWith("bridge:") && value.Status == "healthy"),
        steps = new[]
        {
            new { key = "domain", name = "HTTPS domain", complete = report.Checks.FirstOrDefault(value => value.Key == "public-url")?.Status == "healthy", detail = "Caddy terminates HTTPS for the panel." },
            new { key = "discord", name = "Discord staff login", complete = report.Checks.FirstOrDefault(value => value.Key == "discord-oauth")?.Status == "healthy", detail = "Configure OAuth credentials in .env." },
            new { key = "server", name = "Register a game server", complete = definitions.Count > 0, detail = "Add the first SCP:SL server from Servers." },
            new { key = "bridge", name = "Connect the LabAPI bridge", complete = report.Checks.Any(value => value.Key.StartsWith("bridge:") && value.Status == "healthy"), detail = "Install the bridge and verify its heartbeat." },
            new { key = "backup", name = "Create a recovery backup", complete = Directory.Exists(Path.Combine(store.StoragePath(), "panel-backups")), detail = "Create and download a verified recovery archive." }
        }
    });
}).RequireAuthorization("Owner");
api.MapGet("/system/backups", (PanelBackupService backups) => backups.List()).RequireAuthorization("Owner");
api.MapPost("/system/backups", async (PanelBackupService backups, AuditService audit, ClaimsPrincipal user, CancellationToken cancellationToken) =>
{
    var result = await backups.CreateAsync(cancellationToken);
    await audit.AddAsync(Actor(user), "panel.backup", result.FileName, result.Verified ? "Created and verified" : "Verification failed");
    return Results.Ok(result);
}).RequireAuthorization("Owner");
api.MapPost("/system/backups/{fileName}/verify", async (string fileName, PanelBackupService backups) =>
    Results.Ok(new { verified = await backups.VerifyAsync(fileName) })).RequireAuthorization("Owner");
api.MapGet("/system/backups/{fileName}", (string fileName, PanelBackupService backups) =>
{
    var path = backups.PathFor(fileName);
    return File.Exists(path) ? Results.File(path, fileName.EndsWith(".aes") ? "application/octet-stream" : "application/zip", Path.GetFileName(path)) : Results.NotFound();
}).RequireAuthorization("Owner");
api.MapGet("/system/update/preflight", async (DeploymentHealthService health, PanelBackupService backups, ServerManager servers) =>
{
    var report = await health.CheckAsync();
    var snapshots = await servers.SnapshotsAsync();
    var latest = backups.List().FirstOrDefault();
    var backupVerified = latest is not null && latest.CreatedAt > DateTimeOffset.UtcNow.AddDays(-1)
        && await backups.VerifyAsync(latest.FileName);
    var checks = new[]
    {
        new { name = "Deployment health", passed = report.Status != "critical", detail = report.Status },
        new { name = "Current recovery archive", passed = backupVerified, detail = latest is null ? "No panel backup" : $"{latest.FileName} ({latest.CreatedAt:O})" },
        new { name = "Game servers stopped", passed = snapshots.All(value => value.State.ToString().Equals("offline", StringComparison.OrdinalIgnoreCase)), detail = $"{snapshots.Count(value => !value.State.ToString().Equals("offline", StringComparison.OrdinalIgnoreCase))} running" },
        new { name = "Production frontend", passed = File.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot", "index.html")), detail = "Built static application" }
    };
    return Results.Ok(new { ready = checks.All(value => value.passed), checkedAt = DateTimeOffset.UtcNow, checks });
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

public partial class Program { }
