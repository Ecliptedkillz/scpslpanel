using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed class BootstrapService(
    JsonStore store, PasswordService passwords, IConfiguration configuration,
    ServerManager servers, ILogger<BootstrapService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var users = await store.ReadAsync<PanelUser>("users", cancellationToken);
        if (users.Count == 0)
        {
            var username = configuration["Panel:BootstrapUsername"] ?? "admin";
            var password = configuration["Panel:BootstrapPassword"] ?? "change-me-now";
            users.Add(new(Guid.NewGuid(), username, passwords.Hash(password), "Owner", true, DateTimeOffset.UtcNow));
            await store.WriteAsync("users", users, cancellationToken);
            logger.LogWarning("Created bootstrap owner account '{Username}'. Change its password immediately.", username);
        }
        await servers.InitializeAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
