using System.IO.Compression;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed record PanelBackupInfo(string FileName, DateTimeOffset CreatedAt, long SizeBytes, bool Verified);

public sealed class PanelBackupService(JsonStore store, IConfiguration configuration, ILogger<PanelBackupService> logger) : BackgroundService
{
    private string Source => store.StoragePath();
    private string Destination => Path.GetFullPath(configuration["Panel:Backups:Path"] ?? Path.Combine(Source, "panel-backups"));
    private int Retention => Math.Clamp(configuration.GetValue("Panel:Backups:Retention", 14), 2, 365);

    public async Task<PanelBackupInfo> CreateAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Source); Directory.CreateDirectory(Destination);
        var created = DateTimeOffset.UtcNow;
        var path = Path.Combine(Destination, $"panel-{created:yyyyMMdd-HHmmss}.zip");
        await Task.Run(() =>
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var file in Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(file);
                if (full.StartsWith(Destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                archive.CreateEntryFromFile(full, Path.GetRelativePath(Source, full), CompressionLevel.SmallestSize);
            }
        }, cancellationToken);
        foreach (var old in Directory.EnumerateFiles(Destination, "panel-*.zip").Select(value => new FileInfo(value))
            .OrderByDescending(value => value.CreationTimeUtc).Skip(Retention)) old.Delete();
        var info = new FileInfo(path);
        return new(info.Name, created, info.Length, await VerifyAsync(info.Name, cancellationToken));
    }

    public IReadOnlyList<PanelBackupInfo> List() => Directory.Exists(Destination)
        ? Directory.EnumerateFiles(Destination, "panel-*.zip").Select(path => new FileInfo(path))
            .OrderByDescending(value => value.CreationTimeUtc)
            .Select(value => new PanelBackupInfo(value.Name, value.CreationTimeUtc, value.Length, false)).ToArray() : [];

    public string PathFor(string name) => Path.Combine(Destination, Path.GetFileName(name));

    public async Task<bool> VerifyAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(PathFor(name));
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            return archive.Entries.Count > 0 && archive.Entries.All(entry =>
                !Path.IsPathRooted(entry.FullName) && !entry.FullName.Split('/').Contains(".."));
        }
        catch (Exception exception) { logger.LogWarning(exception, "Panel backup verification failed"); return false; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (configuration.GetValue("Panel:Backups:Enabled", true)
                    && (!List().Any() || List()[0].CreatedAt < DateTimeOffset.UtcNow.AddHours(-24)))
                    await CreateAsync(stoppingToken);
            }
            catch (Exception exception) { logger.LogError(exception, "Scheduled panel backup failed"); }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
