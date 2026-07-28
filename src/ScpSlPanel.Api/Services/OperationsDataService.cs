using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed class OperationsDataService(JsonStore store)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _logGates = new();

    public async Task AppendConsoleAsync(Guid serverId, string stream, string line)
    {
        var directory = store.StoragePath("logs", serverId.ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
        var entry = JsonSerializer.Serialize(new ConsoleLogEntry(DateTimeOffset.UtcNow, stream, line));
        var gate = _logGates.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { await File.AppendAllTextAsync(path, entry + Environment.NewLine); }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<ConsoleLogEntry>> ConsoleAsync(Guid serverId, int take, string? search)
    {
        var directory = store.StoragePath("logs", serverId.ToString("N"));
        if (!Directory.Exists(directory)) return [];
        var files = Directory.EnumerateFiles(directory, "*.jsonl").OrderByDescending(x => x).Take(7);
        var entries = new List<ConsoleLogEntry>();
        foreach (var file in files)
        {
            foreach (var line in await File.ReadAllLinesAsync(file))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<ConsoleLogEntry>(line);
                    if (entry is not null && (string.IsNullOrWhiteSpace(search)
                        || entry.Line.Contains(search, StringComparison.OrdinalIgnoreCase))) entries.Add(entry);
                }
                catch { }
            }
        }
        return entries.OrderByDescending(x => x.At).Take(Math.Clamp(take, 1, 5000)).Reverse().ToList();
    }

    public async Task AddMetricAsync(ServerSnapshot snapshot, bool bridgeConnected)
    {
        var metrics = await store.ReadAsync<MetricSample>("metrics");
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        metrics.RemoveAll(x => x.At < cutoff);
        metrics.Add(new(snapshot.Id, DateTimeOffset.UtcNow, snapshot.CpuPercent, snapshot.MemoryBytes,
            snapshot.Players, snapshot.State, bridgeConnected));
        await store.WriteAsync("metrics", metrics);
    }

    public async Task<IReadOnlyList<MetricSample>> MetricsAsync(Guid serverId, int hours)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-Math.Clamp(hours, 1, 168));
        return (await store.ReadAsync<MetricSample>("metrics"))
            .Where(x => x.ServerId == serverId && x.At >= cutoff).OrderBy(x => x.At).ToList();
    }

    public async Task AddIncidentAsync(Guid serverId, string type, string message, int? exitCode = null)
    {
        var incidents = await store.ReadAsync<ServerIncident>("incidents");
        incidents.Insert(0, new(Guid.NewGuid(), serverId, DateTimeOffset.UtcNow, type, message, exitCode));
        await store.WriteAsync("incidents", incidents.Take(1000));
    }

    public async Task<IReadOnlyList<ServerIncident>> IncidentsAsync(Guid serverId) =>
        (await store.ReadAsync<ServerIncident>("incidents")).Where(x => x.ServerId == serverId).ToList();

    public async Task<BackupEntry> CreateBackupAsync(ServerDefinition server, string actor)
    {
        var directory = store.StoragePath("backups", server.Id.ToString("N"));
        Directory.CreateDirectory(directory);
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
        var path = Path.Combine(directory, name);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".txt", ".yml", ".yaml", ".json", ".cfg", ".ini", ".toml", ".csv" };
            foreach (var file in Directory.EnumerateFiles(server.WorkingDirectory, "*", SearchOption.AllDirectories)
                .Where(file => extensions.Contains(Path.GetExtension(file))).Take(10000))
            {
                try { archive.CreateEntryFromFile(file, Path.GetRelativePath(server.WorkingDirectory, file), CompressionLevel.Fastest); }
                catch (IOException) { }
            }
        }
        var entry = new BackupEntry(Guid.NewGuid(), server.Id, DateTimeOffset.UtcNow, name,
            new FileInfo(path).Length, actor);
        var backups = await store.ReadAsync<BackupEntry>("backups");
        backups.Insert(0, entry);
        await store.WriteAsync("backups", backups);
        return entry;
    }

    public async Task<IReadOnlyList<BackupEntry>> BackupsAsync(Guid serverId) =>
        (await store.ReadAsync<BackupEntry>("backups")).Where(x => x.ServerId == serverId).ToList();

    public string BackupPath(Guid serverId, string fileName) =>
        Path.Combine(store.StoragePath("backups", serverId.ToString("N")), Path.GetFileName(fileName));
}
