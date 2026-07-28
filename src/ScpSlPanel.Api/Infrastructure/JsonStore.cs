using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ScpSlPanel.Api.Infrastructure;

public sealed class JsonStore(IHostEnvironment environment, IConfiguration configuration)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root = Path.GetFullPath(Path.Combine(
        environment.ContentRootPath, configuration["Panel:DataPath"] ?? "data"));
    private readonly bool _sqlite = configuration["Panel:StorageProvider"]?.Equals(
        "sqlite", StringComparison.OrdinalIgnoreCase) == true;
    private string ConnectionString => new SqliteConnectionStringBuilder {
        DataSource = Path.Combine(_root, "panel.db"), Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString();

    public async Task<List<T>> ReadAsync<T>(string collection, CancellationToken cancellationToken = default)
    {
        if (_sqlite) return await ReadSqliteAsync<T>(collection, cancellationToken);
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
        if (_sqlite)
        {
            await WriteSqliteAsync(collection, items, cancellationToken);
            return;
        }
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

    private async Task<List<T>> ReadSqliteAsync<T>(string collection, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM collections WHERE name = $name";
            command.Parameters.AddWithValue("$name", collection);
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
            if (json is not null) return JsonSerializer.Deserialize<List<T>>(json, _json) ?? [];

            var legacyPath = PathFor(collection);
            if (!File.Exists(legacyPath)) return [];
            json = await File.ReadAllTextAsync(legacyPath, cancellationToken);
            var values = JsonSerializer.Deserialize<List<T>>(json, _json) ?? [];
            await UpsertAsync(connection, collection, JsonSerializer.Serialize(values, _json), cancellationToken);
            return values;
        }
        finally { _gate.Release(); }
    }

    private async Task WriteSqliteAsync<T>(
        string collection, IEnumerable<T> items, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var json = JsonSerializer.Serialize(items, _json);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await UpsertAsync(connection, collection, json, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS collections (
                name TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertAsync(
        SqliteConnection connection, string collection, string json, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO collections(name, json, updated_at) VALUES($name, $json, $updated)
            ON CONFLICT(name) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$name", collection);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
