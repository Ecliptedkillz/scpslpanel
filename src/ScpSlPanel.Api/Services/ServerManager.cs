using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Hubs;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed class ServerManager(
    JsonStore store, IHubContext<PanelHub> hub, AuditService audit, BridgeStateService bridge,
    ILogger<ServerManager> logger)
{
    private sealed class Runtime
    {
        public Process? Process;
        public ServerState State = ServerState.Offline;
        public DateTimeOffset? StartedAt;
        public string? LastError;
        public long PreviousCpuTicks;
        public DateTimeOffset PreviousSample = DateTimeOffset.UtcNow;
        public bool StopInProgress;
    }

    private readonly ConcurrentDictionary<Guid, Runtime> _runtime = new();

    public Task<List<ServerDefinition>> DefinitionsAsync() => store.ReadAsync<ServerDefinition>("servers");

    public async Task<ServerDefinition?> FindAsync(Guid id) =>
        (await DefinitionsAsync()).FirstOrDefault(x => x.Id == id);

    public async Task<ServerDefinition> AddAsync(ServerCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ExecutablePath))
            throw new ArgumentException("Name and executable path are required.");
        var definitions = await DefinitionsAsync();
        var working = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? Path.GetDirectoryName(Path.GetFullPath(request.ExecutablePath))!
            : Path.GetFullPath(request.WorkingDirectory);
        var item = new ServerDefinition(Guid.NewGuid(), request.Name.Trim(), Path.GetFullPath(request.ExecutablePath),
            request.Arguments ?? "", working, request.AutoRestart, request.AutoStart, request.QueryPort,
            request.UpdateCommand, DateTimeOffset.UtcNow, CreateBridgeToken());
        definitions.Add(item);
        await store.WriteAsync("servers", definitions);
        return item;
    }

    public async Task<bool> RemoveAsync(Guid id)
    {
        if (_runtime.TryGetValue(id, out var active) && active.Process is { HasExited: false }) return false;
        var definitions = await DefinitionsAsync();
        var removed = definitions.RemoveAll(x => x.Id == id) > 0;
        if (removed) await store.WriteAsync("servers", definitions);
        _runtime.TryRemove(id, out _);
        return removed;
    }

    public async Task<IReadOnlyList<ServerSnapshot>> SnapshotsAsync()
    {
        var definitions = await DefinitionsAsync();
        return definitions.Select(Snapshot).ToList();
    }

    public async Task<ServerSnapshot?> SnapshotAsync(Guid id)
    {
        var definition = await FindAsync(id);
        return definition is null ? null : Snapshot(definition);
    }

    private ServerSnapshot Snapshot(ServerDefinition definition)
    {
        var runtime = _runtime.GetOrAdd(definition.Id, _ => new Runtime());
        var process = runtime.Process;
        long memory = 0;
        double cpu = 0;
        int? processId = null;
        if (process is { HasExited: false })
        {
            try
            {
                process.Refresh();
                memory = process.WorkingSet64;
                processId = process.Id;
                var now = DateTimeOffset.UtcNow;
                var ticks = process.TotalProcessorTime.Ticks;
                var elapsed = (now - runtime.PreviousSample).TotalMilliseconds;
                if (elapsed > 0 && runtime.PreviousCpuTicks > 0)
                    cpu = Math.Clamp((ticks - runtime.PreviousCpuTicks) / TimeSpan.TicksPerMillisecond /
                        elapsed / Environment.ProcessorCount * 100, 0, 100);
                runtime.PreviousCpuTicks = ticks;
                runtime.PreviousSample = now;
            }
            catch { /* Process may have exited during sampling. */ }
        }
        var bridgeStatus = bridge.Get(definition.Id);
        return new(definition.Id, definition.Name, runtime.State, processId, runtime.StartedAt,
            memory, Math.Round(cpu, 1), bridgeStatus.Players.Count, bridgeStatus.MaxPlayers, runtime.LastError);
    }

    public async Task<string> EnsureBridgeTokenAsync(Guid id, bool regenerate = false)
    {
        var definitions = await DefinitionsAsync();
        var index = definitions.FindIndex(item => item.Id == id);
        if (index < 0) throw new KeyNotFoundException("Server not found.");
        var token = regenerate || string.IsNullOrWhiteSpace(definitions[index].BridgeToken)
            ? CreateBridgeToken()
            : definitions[index].BridgeToken!;
        if (token != definitions[index].BridgeToken)
        {
            definitions[index] = definitions[index] with { BridgeToken = token };
            await store.WriteAsync("servers", definitions);
        }
        return token;
    }

    public async Task<bool> ValidateBridgeTokenAsync(Guid id, string? supplied)
    {
        if (string.IsNullOrWhiteSpace(supplied)) return false;
        var definition = await FindAsync(id);
        if (string.IsNullOrWhiteSpace(definition?.BridgeToken)) return false;
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(definition.BridgeToken);
        var suppliedBytes = System.Text.Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static string CreateBridgeToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task StartAsync(Guid id, string actor)
    {
        var definition = await FindAsync(id) ?? throw new KeyNotFoundException("Server not found.");
        var runtime = _runtime.GetOrAdd(id, _ => new Runtime());
        lock (runtime)
        {
            if (runtime.StopInProgress)
                throw new InvalidOperationException("The server is still stopping. Try again in a moment.");
            if (runtime.Process is { HasExited: false }) throw new InvalidOperationException("Server is already running.");
            if (!File.Exists(definition.ExecutablePath)) throw new FileNotFoundException("Server executable was not found.", definition.ExecutablePath);
            runtime.State = ServerState.Starting;
            runtime.LastError = null;
            var start = new ProcessStartInfo
            {
                FileName = definition.ExecutablePath,
                Arguments = definition.Arguments,
                WorkingDirectory = definition.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => PublishLine(id, "stdout", e.Data);
            process.ErrorDataReceived += (_, e) => PublishLine(id, "stderr", e.Data);
            process.Exited += (_, _) => _ = OnExitedAsync(definition, runtime, process);
            if (!process.Start()) throw new InvalidOperationException("The server process did not start.");
            runtime.Process = process;
            runtime.StartedAt = DateTimeOffset.UtcNow;
            runtime.State = ServerState.Online;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        await audit.AddAsync(actor, "server.start", definition.Name, $"Started server {definition.Name}");
        await hub.Clients.All.SendAsync("ServerChanged", Snapshot(definition));
    }

    public async Task StopAsync(Guid id, string actor, bool force = false)
    {
        var definition = await FindAsync(id) ?? throw new KeyNotFoundException("Server not found.");
        var runtime = _runtime.GetOrAdd(id, _ => new Runtime());
        Process? process;
        lock (runtime)
        {
            process = runtime.Process;
            if (process is null) return;
            if (runtime.StopInProgress)
                throw new InvalidOperationException("A stop or restart operation is already in progress.");
            try
            {
                if (process.HasExited) return;
            }
            catch (InvalidOperationException) { return; }
            runtime.StopInProgress = true;
            runtime.State = ServerState.Stopping;
        }
        try
        {
            if (!force)
            {
                try { await process.StandardInput.WriteLineAsync("shutdown"); }
                catch { force = true; }
                if (!force)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException) { force = true; }
                }
            }
            if (force && !SafeHasExited(process))
            {
                process.Kill(entireProcessTree: true);
                using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(killTimeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException("The server process did not exit after it was terminated.");
                }
            }
            await audit.AddAsync(actor, force ? "server.kill" : "server.stop", definition.Name, "Stopped server");
        }
        finally
        {
            lock (runtime)
            {
                if (ReferenceEquals(runtime.Process, process))
                    runtime.Process = null;
                runtime.State = ServerState.Offline;
                runtime.StopInProgress = false;
            }
            process.Dispose();
        }
    }

    public async Task RestartAsync(Guid id, string actor)
    {
        await StopAsync(id, actor);
        await StartAsync(id, actor);
    }

    public async Task CommandAsync(Guid id, string command, string actor)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command cannot be empty.");
        var definition = await FindAsync(id) ?? throw new KeyNotFoundException("Server not found.");
        var runtime = _runtime.GetOrAdd(id, _ => new Runtime());
        if (runtime.Process is not { HasExited: false } process) throw new InvalidOperationException("Server is offline.");
        await process.StandardInput.WriteLineAsync(command);
        await PublishLine(id, "command", $"> {command}");
        await audit.AddAsync(actor, "console.command", definition.Name, command);
    }

    public async Task InitializeAsync()
    {
        foreach (var definition in await DefinitionsAsync())
            if (definition.AutoStart)
                try { await StartAsync(definition.Id, "system"); }
                catch (Exception ex) { logger.LogError(ex, "Failed to auto-start {Server}", definition.Name); }
    }

    private Task PublishLine(Guid id, string stream, string? line) =>
        line is null ? Task.CompletedTask : hub.Clients.Group($"server:{id}")
            .SendAsync("ConsoleLine", new { serverId = id, stream, line, at = DateTimeOffset.UtcNow });

    private async Task OnExitedAsync(ServerDefinition definition, Runtime runtime, Process exitedProcess)
    {
        ServerState priorState;
        int? exitCode;
        bool disposeProcess;
        lock (runtime)
        {
            if (!ReferenceEquals(runtime.Process, exitedProcess))
            {
                return;
            }
            priorState = runtime.State;
            try { exitCode = exitedProcess.ExitCode; }
            catch { exitCode = null; }
            runtime.State = ServerState.Offline;
            runtime.Process = null;
            disposeProcess = !runtime.StopInProgress;
        }
        if (disposeProcess) exitedProcess.Dispose();
        await PublishLine(definition.Id, "system", $"Process exited with code {exitCode}.");
        await hub.Clients.All.SendAsync("ServerChanged", Snapshot(definition));
        if (definition.AutoRestart && priorState != ServerState.Stopping)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            try { await StartAsync(definition.Id, "system:auto-restart"); }
            catch (Exception ex)
            {
                runtime.State = ServerState.Faulted;
                runtime.LastError = ex.Message;
                logger.LogError(ex, "Auto-restart failed for {Server}", definition.Name);
            }
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }
}
