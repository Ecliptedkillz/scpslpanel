using Discord;
using Discord.WebSocket;
using ScpSlPanel.Api.Domain;

namespace ScpSlPanel.Api.Services;

public sealed class DiscordBotService(
    NotificationService settingsService, ServerManager servers, BridgeStateService bridge,
    AuditService audit, ILogger<DiscordBotService> logger) : BackgroundService
{
    private DiscordSocketClient? _client;
    private string _activeToken = "";
    private volatile string? _error;

    public DiscordBotStatus Status
    {
        get
        {
            var settings = settingsService.GetAsync().GetAwaiter().GetResult();
            return new(settings.DiscordBotEnabled, _client?.ConnectionState == ConnectionState.Connected,
                _client?.CurrentUser?.Username, _error);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await settingsService.GetAsync();
                if (!settings.DiscordBotEnabled || string.IsNullOrWhiteSpace(settings.DiscordBotToken))
                    await DisconnectAsync();
                else if (_client is null || _activeToken != settings.DiscordBotToken)
                    await ConnectAsync(settings.DiscordBotToken);
            }
            catch (Exception ex) { _error = ex.Message; logger.LogError(ex, "Discord bot connection failed"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        await DisconnectAsync();
    }

    private async Task ConnectAsync(string token)
    {
        await DisconnectAsync();
        var client = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds });
        client.Log += message => { logger.Log((LogLevel)Math.Max(0, (int)LogLevel.Critical - (int)message.Severity), "{Message}", message.Message); return Task.CompletedTask; };
        client.Ready += RegisterCommandsAsync;
        client.SlashCommandExecuted += HandleCommandAsync;
        _client = client;
        _activeToken = token;
        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();
        _error = null;
    }

    private async Task DisconnectAsync()
    {
        if (_client is null) return;
        try { await _client.StopAsync(); await _client.LogoutAsync(); }
        catch { }
        _client.Dispose();
        _client = null;
        _activeToken = "";
    }

    private async Task RegisterCommandsAsync()
    {
        var settings = await settingsService.GetAsync();
        if (_client is null || settings.DiscordGuildId == 0) return;
        var guild = _client.GetGuild(settings.DiscordGuildId);
        if (guild is null) { _error = "The configured Discord guild was not found."; return; }
        var serverOption = new SlashCommandOptionBuilder().WithName("server").WithDescription("Registered server name").WithType(ApplicationCommandOptionType.String).WithRequired(true);
        var command = new SlashCommandBuilder().WithName("scp").WithDescription("SCP Control panel commands")
            .AddOption(new SlashCommandOptionBuilder().WithName("status").WithDescription("Show server status").WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder().WithName("players").WithDescription("Show connected players").WithType(ApplicationCommandOptionType.SubCommand).AddOption(serverOption))
            .AddOption(Subcommand("start", "Start a server"))
            .AddOption(Subcommand("stop", "Stop a server"))
            .AddOption(Subcommand("restart", "Restart a server"))
            .AddOption(new SlashCommandOptionBuilder().WithName("announce").WithDescription("Send an in-game announcement").WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(serverOption).AddOption("message", ApplicationCommandOptionType.String, "Announcement text", isRequired:true));
        await guild.BulkOverwriteApplicationCommandAsync([command.Build()]);
    }

    private static SlashCommandOptionBuilder Subcommand(string name, string description) =>
        new SlashCommandOptionBuilder().WithName(name).WithDescription(description)
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption("server", ApplicationCommandOptionType.String, "Registered server name", isRequired:true);

    private async Task HandleCommandAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral:true);
        try
        {
            var sub = command.Data.Options.First();
            var action = sub.Name;
            var settings = await settingsService.GetAsync();
            if (action is not ("status" or "players") && !Allowed(command.User, settings))
            {
                await command.ModifyOriginalResponseAsync(x => x.Content = "You do not have a configured SCP Control role.");
                return;
            }
            var definitions = await servers.DefinitionsAsync();
            if (action == "status")
            {
                var snapshots = await servers.SnapshotsAsync();
                var text = snapshots.Count == 0 ? "No servers are registered." : string.Join('\n',
                    snapshots.Select(x => $"**{x.Name}** — {x.State}, {x.Players}/{x.MaxPlayers} players, {x.MemoryBytes / 1024 / 1024} MB"));
                await command.ModifyOriginalResponseAsync(x => x.Content = text);
                return;
            }
            var name = Convert.ToString(sub.Options.First(x => x.Name == "server").Value) ?? "";
            var server = definitions.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (server is null)
            {
                await command.ModifyOriginalResponseAsync(x => x.Content = $"Server `{name}` was not found.");
                return;
            }
            if (action == "players")
            {
                var players = bridge.Get(server.Id).Players;
                var text = players.Count == 0 ? $"No players are connected to **{server.Name}**."
                    : string.Join('\n', players.Take(30).Select(x => $"• {x.Nickname} — {x.Role} ({x.Ping} ms)"));
                await command.ModifyOriginalResponseAsync(x => x.Content = text);
                return;
            }
            var actor = $"discord:{command.User.Username}:{command.User.Id}";
            if (action == "start") await servers.StartAsync(server.Id, actor);
            else if (action == "stop") await servers.StopAsync(server.Id, actor);
            else if (action == "restart") await servers.RestartAsync(server.Id, actor);
            else if (action == "announce")
            {
                var message = Convert.ToString(sub.Options.First(x => x.Name == "message").Value) ?? "";
                await servers.CommandAsync(server.Id, $"bc 10 {message}", actor);
            }
            await audit.AddAsync(actor, $"discord.{action}", server.Name, "Discord slash command");
            await command.ModifyOriginalResponseAsync(x => x.Content = $"`{action}` accepted for **{server.Name}**.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord command failed");
            await command.ModifyOriginalResponseAsync(x => x.Content = $"Command failed: {ex.Message}");
        }
    }

    private static bool Allowed(SocketUser user, PanelIntegrationSettings settings)
    {
        if (user is not SocketGuildUser guildUser) return false;
        if (guildUser.GuildPermissions.Administrator) return true;
        var allowed = settings.DiscordControlRoleIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => ulong.TryParse(value, out var id) ? id : 0).ToHashSet();
        return guildUser.Roles.Any(role => allowed.Contains(role.Id));
    }
}
