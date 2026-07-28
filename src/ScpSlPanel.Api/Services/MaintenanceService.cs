using System.Diagnostics;
using ScpSlPanel.Api.Domain;

namespace ScpSlPanel.Api.Services;

public sealed class MaintenanceService(
    ServerManager servers, OperationsDataService operations, AuditService audit)
{
    public async Task<BackupEntry> BackupAsync(Guid serverId, string actor)
    {
        var server = await servers.FindAsync(serverId) ?? throw new KeyNotFoundException("Server not found.");
        var entry = await operations.CreateBackupAsync(server, actor);
        await audit.AddAsync(actor, "server.backup", server.Name, entry.FileName);
        return entry;
    }

    public async Task<string> UpdateAsync(Guid serverId, string actor)
    {
        var server = await servers.FindAsync(serverId) ?? throw new KeyNotFoundException("Server not found.");
        if (string.IsNullOrWhiteSpace(server.UpdateCommand))
            throw new InvalidOperationException("No update command is configured for this server.");
        var snapshot = await servers.SnapshotAsync(serverId);
        if (snapshot?.State != ServerState.Offline)
            throw new InvalidOperationException("Stop the server before running its update command.");
        await BackupAsync(serverId, actor);
        var start = new ProcessStartInfo
        {
            FileName = "cmd.exe", Arguments = $"/d /s /c \"{server.UpdateCommand}\"",
            WorkingDirectory = server.WorkingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Update process did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        await process.WaitForExitAsync(timeout.Token);
        var output = (await outputTask) + Environment.NewLine + (await errorTask);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Update failed with exit code {process.ExitCode}: {output[^Math.Min(output.Length, 2000)..]}");
        await audit.AddAsync(actor, "server.update", server.Name, "Update completed");
        return output[^Math.Min(output.Length, 5000)..];
    }

    public async Task<BackupEntry> RestoreAsync(Guid serverId, string fileName, string actor)
    {
        var server = await servers.FindAsync(serverId) ?? throw new KeyNotFoundException("Server not found.");
        var snapshot = await servers.SnapshotAsync(serverId);
        if (snapshot?.State != ServerState.Offline)
            throw new InvalidOperationException("Stop the server before restoring a backup.");
        var safetyBackup = await BackupAsync(serverId, actor);
        await operations.RestoreBackupAsync(server, fileName);
        await audit.AddAsync(actor, "server.backup.restore", server.Name,
            $"Restored {Path.GetFileName(fileName)}; safety backup {safetyBackup.FileName}");
        return safetyBackup;
    }
}
