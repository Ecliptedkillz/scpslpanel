using System.Text.Json;

namespace ScpSlPanel.Api.Infrastructure;

public sealed class JsonStore(IHostEnvironment environment, IConfiguration configuration)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root = Path.GetFullPath(Path.Combine(
        environment.ContentRootPath, configuration["Panel:DataPath"] ?? "data"));

    public async Task<List<T>> ReadAsync<T>(string collection, CancellationToken cancellationToken = default)
    {
        var path = PathFor(collection);
        if (!File.Exists(path)) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<T>>(stream, _json, cancellationToken) ?? [];
        }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync<T>(string collection, IEnumerable<T> items, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var path = PathFor(collection);
        var temp = path + ".tmp";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, items, _json, cancellationToken);
            File.Move(temp, path, true);
        }
        finally { _gate.Release(); }
    }

    public string ResolveSafePath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The requested path is outside the server directory.");
        return fullPath;
    }

    public string StoragePath(params string[] parts)
    {
        var path = Path.GetFullPath(parts.Aggregate(_root, Path.Combine));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(path, _root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The storage path is outside the panel data directory.");
        return path;
    }

    private string PathFor(string collection) => Path.Combine(_root, collection + ".json");
}
