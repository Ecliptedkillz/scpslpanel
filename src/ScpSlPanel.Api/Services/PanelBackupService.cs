using System.IO.Compression;
using System.Security.Cryptography;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed record PanelBackupInfo(string FileName, DateTimeOffset CreatedAt, long SizeBytes, bool Verified, bool Encrypted);

public sealed class PanelBackupService(JsonStore store, IConfiguration configuration, ILogger<PanelBackupService> logger) : BackgroundService
{
    private string Source => store.StoragePath();
    private string Destination => Path.GetFullPath(string.IsNullOrWhiteSpace(configuration["Panel:Backups:Path"])
        ? Path.Combine(Source, "panel-backups") : configuration["Panel:Backups:Path"]!);
    private int Retention => Math.Clamp(configuration.GetValue("Panel:Backups:Retention", 14), 2, 365);
    public bool EncryptionConfigured => TryGetKey(out _);

    public async Task<PanelBackupInfo> CreateAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Source); Directory.CreateDirectory(Destination);
        var created = DateTimeOffset.UtcNow;
        var zipPath = Path.Combine(Destination, $".panel-{Guid.NewGuid():N}.zip.tmp");
        await Task.Run(() =>
        {
            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var file in Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(file);
                if (full.StartsWith(Destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                archive.CreateEntryFromFile(full, Path.GetRelativePath(Source, full), CompressionLevel.SmallestSize);
            }
        }, cancellationToken);
        var encrypted = TryGetKey(out var key);
        var path = Path.Combine(Destination, $"panel-{created:yyyyMMdd-HHmmss}.zip{(encrypted ? ".aes" : "")}");
        if (encrypted)
        {
            var plain = await File.ReadAllBytesAsync(zipPath, cancellationToken);
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[plain.Length];
                using var aes = new AesGcm(key!, 16); aes.Encrypt(nonce, plain, cipher, tag);
                await using var output = File.Create(path);
                await output.WriteAsync("SCPB1"u8.ToArray(), cancellationToken);
                await output.WriteAsync(nonce, cancellationToken); await output.WriteAsync(tag, cancellationToken);
                await output.WriteAsync(cipher, cancellationToken);
            }
            finally { CryptographicOperations.ZeroMemory(plain); File.Delete(zipPath); }
        }
        else File.Move(zipPath, path, true);
        foreach (var old in Directory.EnumerateFiles(Destination, "panel-*.zip*").Select(value => new FileInfo(value))
            .OrderByDescending(value => value.CreationTimeUtc).Skip(Retention)) old.Delete();
        var info = new FileInfo(path);
        return new(info.Name, created, info.Length, await VerifyAsync(info.Name, cancellationToken), encrypted);
    }

    public IReadOnlyList<PanelBackupInfo> List() => Directory.Exists(Destination)
        ? Directory.EnumerateFiles(Destination, "panel-*.zip*").Select(path => new FileInfo(path))
            .OrderByDescending(value => value.CreationTimeUtc)
            .Select(value => new PanelBackupInfo(value.Name, value.CreationTimeUtc, value.Length, false, value.Name.EndsWith(".aes"))).ToArray() : [];

    public string PathFor(string name) => Path.Combine(Destination, Path.GetFileName(name));

    public async Task<bool> VerifyAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            Stream stream;
            byte[]? plain = null;
            if (name.EndsWith(".aes", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetKey(out var key)) return false;
                var payload = await File.ReadAllBytesAsync(PathFor(name), cancellationToken);
                if (payload.Length < 34 || !payload.AsSpan(0, 5).SequenceEqual("SCPB1"u8)) return false;
                plain = new byte[payload.Length - 33];
                using var aes = new AesGcm(key!, 16);
                aes.Decrypt(payload.AsSpan(5, 12), payload.AsSpan(33), payload.AsSpan(17, 16), plain);
                stream = new MemoryStream(plain, false);
            }
            else stream = File.OpenRead(PathFor(name));
            using (stream)
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                if (archive.Entries.Count == 0 || archive.Entries.Any(entry =>
                    Path.IsPathRooted(entry.FullName) || entry.FullName.Split('/').Contains(".."))) return false;
                foreach (var entry in archive.Entries.Where(value => !string.IsNullOrEmpty(value.Name)))
                {
                    await using var content = entry.Open();
                    await content.CopyToAsync(Stream.Null, cancellationToken);
                }
                return true;
            }
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

    private bool TryGetKey(out byte[]? key)
    {
        key = null;
        try { key = Convert.FromBase64String(configuration["Panel:Backups:EncryptionKey"] ?? ""); }
        catch (FormatException) { }
        return key?.Length is 16 or 24 or 32;
    }
}
