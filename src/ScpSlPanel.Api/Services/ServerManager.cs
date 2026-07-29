using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using ScpSlPanel.Api.Domain;
using ScpSlPanel.Api.Hubs;
using ScpSlPanel.Api.Infrastructure;

namespace ScpSlPanel.Api.Services;

public sealed class ServerManager(
    JsonStore store, IHubContext<PanelHub> hub, AuditService audit, BridgeStateService bridge,
    OperationsDataService operations, NotificationService notifications, ILogger<ServerManager> logger)
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
        public bool Reattached;
        public TaskCompletionSource StopCompletion = CreateCompletedStop();

        private static TaskCompletionSource CreateCompletedStop()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion.SetResult();
            return completion;
        }
    }

    private readonly ConcurrentDictionary<Guid, Runtime> _runtime = new();
    private readonly SemaphoreSlim _processRecordGate = new(1, 1);

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
            request.UpdateCommand, DateTimeOffset.UtcNow, CreateBridgeToken(),
            request.Icon, request.AccentColor);
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
            memory, Math.Round(cpu, 1), bridgeStatus.Players.Count, bridgeStatus.MaxPlayers,
            runtime.LastError, definition.Icon, definition.AccentColor);
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
        await WaitForStopAsync(runtime);
        lock (runtime)
        {
            if (runtime.StopInProgress)
                throw new InvalidOperationException("The server is still stopping. Try again in a moment.");
            if (runtime.Process is { HasExited: false }) throw new InvalidOperationException("Server is already running.");
            if (!File.Exists(definition.ExecutablePath)) throw new FileNotFoundException("Server executable was not found.", definition.ExecutablePath);
            var isLocalAdmin = Path.GetFileNameWithoutExtension(definition.ExecutablePath)
                .Equals("LocalAdmin", StringComparison.OrdinalIgnoreCase);
            if (isLocalAdmin && !File.Exists(Path.Combine(definition.WorkingDirectory, "SCPSL.exe")))
                throw new FileNotFoundException(
                    "LocalAdmin must use the dedicated server folder containing SCPSL.exe as its working directory.",
                    Path.Combine(definition.WorkingDirectory, "SCPSL.exe"));
            if (isLocalAdmin) EnsureLocalAdminHeadlessConfig();
            var arguments = isLocalAdmin && string.IsNullOrWhiteSpace(definition.Arguments)
                ? definition.QueryPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : definition.Arguments;
            runtime.State = ServerState.Starting;
            runtime.LastError = null;
            var start = new ProcessStartInfo
            {
                FileName = definition.ExecutablePath,
                Arguments = arguments,
                WorkingDirectory = definition.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => _ = PublishLine(id, "stdout", e.Data);
            process.ErrorDataReceived += (_, e) => _ = PublishLine(id, "stderr", e.Data);
            process.Exited += (_, _) => _ = OnExitedAsync(definition, runtime, process);
            if (!process.Start()) throw new InvalidOperationException("The server process did not start.");
            runtime.Process = process;
            runtime.Reattached = false;
            runtime.StartedAt = DateTimeOffset.UtcNow;
            runtime.State = ServerState.Online;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        await SaveProcessRecordAsync(definition, runtime.Process!);
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
            runtime.StopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.State = ServerState.Stopping;
        }
        try
        {
            if (runtime.Reattached) force = true;
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
            TaskCompletionSource stopCompletion;
            lock (runtime)
            {
                if (ReferenceEquals(runtime.Process, process))
                    runtime.Process = null;
                runtime.Reattached = false;
                runtime.State = ServerState.Offline;
                runtime.StopInProgress = false;
                stopCompletion = runtime.StopCompletion;
            }
            var stoppedProcessId = process.Id;
            process.Dispose();
            await RemoveProcessRecordAsync(id, stoppedProcessId);
            stopCompletion.TrySetResult();
        }
    }

    public async Task RestartAsync(Guid id, string actor)
    {
        // LocalAdmin can crash while reading redirected console commands and may leave
        // SCPSL.exe behind. A restart must replace the complete managed process tree.
        await StopAsync(id, actor, force: true);
        await Task.Delay(TimeSpan.FromMilliseconds(750));
        await StartAsync(id, actor);
    }

    public async Task CommandAsync(Guid id, string command, string actor)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command cannot be empty.");
        var definition = await FindAsync(id) ?? throw new KeyNotFoundException("Server not found.");
        var runtime = _runtime.GetOrAdd(id, _ => new Runtime());
        if (runtime.Process is not { HasExited: false } process) throw new InvalidOperationException("Server is offline.");
        if (runtime.Reattached)
            throw new InvalidOperationException(
                "The panel reattached to this process after restarting. Restart the game server once to restore console input.");
        await process.StandardInput.WriteLineAsync(command);
        await PublishLine(id, "command", $"> {command}");
        await audit.AddAsync(actor, "console.command", definition.Name, command);
    }

    public async Task InitializeAsync()
    {
        var definitions = await DefinitionsAsync();
        await ReattachProcessesAsync(definitions);
        foreach (var definition in definitions)
            if (definition.AutoStart && !IsRunning(definition.Id))
                try { await StartAsync(definition.Id, "system"); }
                catch (Exception ex) { logger.LogError(ex, "Failed to auto-start {Server}", definition.Name); }
    }

    private async Task PublishLine(Guid id, string stream, string? line)
    {
        if (line is null) return;
        await operations.AppendConsoleAsync(id, stream, line);
        await hub.Clients.Group($"server:{id}")
            .SendAsync("ConsoleLine", new { serverId = id, stream, line, at = DateTimeOffset.UtcNow });
    }

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
            runtime.Reattached = false;
            disposeProcess = !runtime.StopInProgress;
        }
        var exitedProcessId = exitedProcess.Id;
        if (disposeProcess) exitedProcess.Dispose();
        await RemoveProcessRecordAsync(definition.Id, exitedProcessId);
        await PublishLine(definition.Id, "system", $"Process exited with code {exitCode}.");
        if (priorState != ServerState.Stopping)
        {
            var settings = await notifications.GetAsync();
            var message = NotificationService.Format(settings.CrashMessage,
                ("server", definition.Name), ("exitCode", exitCode), ("autoRestart", definition.AutoRestart));
            await operations.AddIncidentAsync(definition.Id, "crash", message, exitCode);
            if (settings.NotifyCrash)
                await notifications.SendAsync($"{definition.Name} crashed", message, "error");
        }
        await hub.Clients.All.SendAsync("ServerChanged", Snapshot(definition));
        if (definition.AutoRestart && priorState != ServerState.Stopping)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            try { await StartAsync(definition.Id, "system:auto-restart"); }
            catch (Exception ex)
            {
                runtime.State = ServerState.Faulted;
                runtime.LastError = ex.Message;
                var message = NotificationService.Format(
                    (await notifications.GetAsync()).RestartFailureMessage,
                    ("server", definition.Name), ("error", ex.Message));
                await operations.AddIncidentAsync(definition.Id, "restart-failure", message);
                await notifications.SendAsync($"{definition.Name}: automatic restart failed", message, "error");
                logger.LogError(ex, "Auto-restart failed for {Server}", definition.Name);
            }
        }
    }

    private bool IsRunning(Guid id) =>
        _runtime.TryGetValue(id, out var runtime)
        && runtime.Process is { } process && !SafeHasExited(process);

    private async Task ReattachProcessesAsync(IReadOnlyList<ServerDefinition> definitions)
    {
        var records = await store.ReadAsync<ManagedProcessRecord>("managed-processes");
        var retained = new List<ManagedProcessRecord>();
        foreach (var record in records)
        {
            var definition = definitions.FirstOrDefault(x => x.Id == record.ServerId);
            if (definition is null) continue;
            Process? process = null;
            try
            {
                process = Process.GetProcessById(record.ProcessId);
                if (process.HasExited) { process.Dispose(); continue; }
                process.Refresh();
                var actualPath = process.MainModule?.FileName;
                var actualStart = new DateTimeOffset(process.StartTime);
                if (string.IsNullOrWhiteSpace(actualPath)
                    || !Path.GetFullPath(actualPath).Equals(
                        Path.GetFullPath(record.ExecutablePath), StringComparison.OrdinalIgnoreCase)
                    || Math.Abs((actualStart - record.StartedAt).TotalSeconds) > 5)
                {
                    logger.LogWarning("Discarded stale PID {ProcessId} for {Server}; executable does not match",
                        record.ProcessId, definition.Name);
                    process.Dispose();
                    continue;
                }
                process.EnableRaisingEvents = true;
                var runtime = _runtime.GetOrAdd(definition.Id, _ => new Runtime());
                runtime.Process = process;
                runtime.Reattached = true;
                runtime.StartedAt = record.StartedAt;
                runtime.State = ServerState.Online;
                runtime.PreviousCpuTicks = process.TotalProcessorTime.Ticks;
                runtime.PreviousSample = DateTimeOffset.UtcNow;
                process.Exited += (_, _) => _ = OnExitedAsync(definition, runtime, process);
                retained.Add(record);
                logger.LogInformation("Reattached {Server} to process {ProcessId}", definition.Name, process.Id);
            }
            catch (Exception ex)
            {
                process?.Dispose();
                logger.LogWarning(ex, "Could not reattach PID {ProcessId} for {Server}",
                    record.ProcessId, definition.Name);
            }
        }
        await store.WriteAsync("managed-processes", retained);
    }

    private async Task SaveProcessRecordAsync(ServerDefinition definition, Process process)
    {
        await _processRecordGate.WaitAsync();
        try
        {
            var records = await store.ReadAsync<ManagedProcessRecord>("managed-processes");
            records.RemoveAll(x => x.ServerId == definition.Id);
            var startedAt = new DateTimeOffset(process.StartTime);
            records.Add(new(definition.Id, process.Id, definition.ExecutablePath, startedAt));
            await store.WriteAsync("managed-processes", records);
        }
        finally { _processRecordGate.Release(); }
    }

    private async Task RemoveProcessRecordAsync(Guid serverId, int processId)
    {
        await _processRecordGate.WaitAsync();
        try
        {
            var records = await store.ReadAsync<ManagedProcessRecord>("managed-processes");
            if (records.RemoveAll(x => x.ServerId == serverId && x.ProcessId == processId) > 0)
                await store.WriteAsync("managed-processes", records);
        }
        finally { _processRecordGate.Release(); }
    }

    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static async Task WaitForStopAsync(Runtime runtime)
    {
        Task waitTask;
        lock (runtime)
        {
            if (!runtime.StopInProgress) return;
            waitTask = runtime.StopCompletion.Task;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await waitTask.WaitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("The previous server process is taking too long to stop.");
        }
    }

    private void EnsureLocalAdminHeadlessConfig()
    {
        var configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCP Secret Laboratory", "config");
        Directory.CreateDirectory(configDirectory);
        var path = Path.Combine(configDirectory, "config_localadmin_global.txt");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var index = lines.FindIndex(line =>
            line.TrimStart().StartsWith("la_no_set_cursor:", StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            if (lines[index].Trim().Equals("la_no_set_cursor: true", StringComparison.OrdinalIgnoreCase))
                return;
            lines[index] = "la_no_set_cursor: true";
        }
        else
        {
            lines.Add("la_no_set_cursor: true");
        }
        File.WriteAllLines(path, lines);
        logger.LogInformation("Enabled headless LocalAdmin console mode in {ConfigPath}", path);
    }
}
