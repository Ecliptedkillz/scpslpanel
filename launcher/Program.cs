using System.Diagnostics;
using System.Drawing;
using System.Text;

ApplicationConfiguration.Initialize();
Application.Run(new LauncherForm());

internal sealed class LauncherForm : Form
{
    private readonly string repositoryRoot = FindRepositoryRoot();
    private readonly string frontendRoot;
    private readonly string apiProject;
    private readonly string npmPath = FindNpmPath();
    private readonly TextBox output = new();
    private readonly Label status = new();
    private readonly List<Button> taskButtons = [];
    private Process? panelProcess;

    public LauncherForm()
    {
        frontendRoot = Path.Combine(repositoryRoot, "src", "scpsl-panel-web");
        apiProject = Path.Combine(repositoryRoot, "src", "ScpSlPanel.Api", "ScpSlPanel.Api.csproj");

        Text = "SCP Control Launcher";
        Width = 840;
        Height = 600;
        MinimumSize = new Size(680, 500);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(9, 11, 15);
        ForeColor = Color.FromArgb(233, 237, 242);

        var heading = new Panel { Dock = DockStyle.Top, Height = 108, Padding = new Padding(24, 18, 24, 12) };
        var eyebrow = new Label { Text = "SERVER OPERATIONS", Dock = DockStyle.Top, Height = 20, ForeColor = Color.FromArgb(228, 67, 67), Font = new Font("Segoe UI", 8, FontStyle.Bold) };
        var title = new Label { Text = "SCP Control", Dock = DockStyle.Top, Height = 37, ForeColor = Color.White, Font = new Font("Segoe UI", 21, FontStyle.Bold) };
        var path = new Label { Text = repositoryRoot, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(119, 128, 141), Font = new Font("Segoe UI", 9), AutoEllipsis = true };
        heading.Controls.Add(path); heading.Controls.Add(title); heading.Controls.Add(eyebrow);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 112, Padding = new Padding(24, 12, 14, 12), WrapContents = true };
        AddButton(toolbar, "Start Panel", async () => await StartPanel(), true);
        AddButton(toolbar, "Stop Panel", () => { StopPanel(); return Task.CompletedTask; });
        AddButton(toolbar, "Open Panel", () => { OpenUrl(GetPanelUrl()); return Task.CompletedTask; });
        AddButton(toolbar, "Build", async () => await BuildAll());
        AddButton(toolbar, "Install Dependencies", async () => await RunCommand(npmPath, "install --no-audit --no-fund", frontendRoot, "Installing frontend dependencies..."));
        AddButton(toolbar, "Update from Git", () => { UpdateFromGit(); return Task.CompletedTask; });

        var statusBar = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(24, 8, 24, 8), BackColor = Color.FromArgb(16, 19, 24) };
        status.Text = "●  READY";
        status.Dock = DockStyle.Fill;
        status.ForeColor = Color.FromArgb(119, 128, 141);
        status.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        statusBar.Controls.Add(status);

        output.Dock = DockStyle.Fill;
        output.Multiline = true;
        output.ReadOnly = true;
        output.ScrollBars = ScrollBars.Vertical;
        output.BackColor = Color.FromArgb(11, 14, 18);
        output.ForeColor = Color.FromArgb(205, 213, 222);
        output.BorderStyle = BorderStyle.FixedSingle;
        output.Font = new Font("Consolas", 10);

        var outputWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 14, 24, 22) };
        outputWrap.Controls.Add(output);
        Controls.Add(outputWrap); Controls.Add(statusBar); Controls.Add(toolbar); Controls.Add(heading);

        FormClosing += (_, _) => { if (panelProcess is { HasExited: false }) panelProcess.Kill(true); };
        AppendLine("Ready. Start Panel will launch the API and serve the built dashboard on port 5080.");
    }

    private void AddButton(FlowLayoutPanel panel, string text, Func<Task> action, bool primary = false)
    {
        var button = new Button {
            Text = text, Width = text.Length > 15 ? 174 : 132, Height = 40, Margin = new Padding(0, 0, 10, 10),
            FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(228, 67, 67) : Color.FromArgb(16, 19, 24),
            ForeColor = Color.White, Font = new Font("Segoe UI", 9, FontStyle.Bold), Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = primary ? Color.FromArgb(241, 90, 90) : Color.FromArgb(36, 42, 50);
        button.Click += async (_, _) => await action();
        taskButtons.Add(button);
        panel.Controls.Add(button);
    }

    private async Task StartPanel()
    {
        await Task.Yield();
        if (panelProcess is { HasExited: false }) { AppendLine("Panel is already running from this launcher."); return; }
        if (!File.Exists(apiProject)) { AppendLine($"API project was not found: {apiProject}"); return; }
        StopProcessesOnPort(5080);
        SetBusy(true, "STARTING");
        try
        {
            var info = new ProcessStartInfo("dotnet", $"run --project \"{apiProject}\" --urls http://127.0.0.1:5080") {
                WorkingDirectory = repositoryRoot, UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            panelProcess = new Process { StartInfo = info, EnableRaisingEvents = true };
            panelProcess.OutputDataReceived += (_, e) => { if (e.Data is not null) AppendLine(e.Data); };
            panelProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) AppendLine(e.Data); };
            panelProcess.Exited += (_, _) => { AppendLine($"Panel exited with code {panelProcess.ExitCode}."); SetStatus("STOPPED", false); };
            panelProcess.Start(); panelProcess.BeginOutputReadLine(); panelProcess.BeginErrorReadLine();
            AppendLine("Starting SCP Control at http://127.0.0.1:5080 ...");
            SetStatus("RUNNING · PORT 5080", true);
        }
        catch (Exception ex) { AppendLine($"Could not start the panel: {ex.Message}"); SetStatus("START FAILED", false); }
        finally { SetBusy(false); }
    }

    private void StopPanel()
    {
        if (panelProcess is { HasExited: false }) { panelProcess.Kill(true); panelProcess.WaitForExit(5000); panelProcess = null; }
        StopProcessesOnPort(5080);
        SetStatus("STOPPED", false);
    }

    private void StopProcessesOnPort(int port)
    {
        try
        {
            var info = new ProcessStartInfo("netstat.exe", "-ano -p tcp") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var netstat = Process.Start(info); if (netstat is null) return;
            var text = netstat.StandardOutput.ReadToEnd(); netstat.WaitForExit();
            var ids = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 5 && parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) && parts[1].EndsWith($":{port}") && parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                .Select(parts => int.TryParse(parts[4], out var id) ? id : 0).Where(id => id > 0).Distinct();
            foreach (var id in ids) { try { using var process = Process.GetProcessById(id); AppendLine($"Stopping PID {id} ({process.ProcessName}) on port {port}..."); process.Kill(true); process.WaitForExit(5000); } catch (Exception ex) { AppendLine($"Could not stop PID {id}: {ex.Message}"); } }
        }
        catch (Exception ex) { AppendLine($"Could not inspect port {port}: {ex.Message}"); }
    }

    private async Task BuildAll()
    {
        if (!await EnsureDependencies()) return;
        if (!await RunCommand(npmPath, "run build", frontendRoot, "Building dashboard...")) return;
        await RunCommand("dotnet", $"build \"{apiProject}\"", repositoryRoot, "Building API...");
    }

    private async Task<bool> EnsureDependencies()
    {
        if (File.Exists(Path.Combine(frontendRoot, "node_modules", ".bin", "vite.cmd"))) return true;
        return await RunCommand(npmPath, "install --no-audit --no-fund", frontendRoot, "Restoring frontend dependencies...");
    }

    private async Task<bool> RunCommand(string file, string arguments, string workingDirectory, string message)
    {
        SetBusy(true, "WORKING"); AppendLine(""); AppendLine(message); AppendLine($"> {file} {arguments}");
        try
        {
            var info = new ProcessStartInfo(file, arguments) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var process = new Process { StartInfo = info };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            foreach (var value in new[] { await stdout, await stderr }) if (!string.IsNullOrWhiteSpace(value)) AppendLine(value.TrimEnd());
            AppendLine(process.ExitCode == 0 ? "Done." : $"Command failed with exit code {process.ExitCode}.");
            return process.ExitCode == 0;
        }
        catch (Exception ex) { AppendLine($"Could not run command: {ex.Message}"); return false; }
        finally { SetBusy(false); }
    }

    private void UpdateFromGit()
    {
        try
        {
            var launcher = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(launcher)) { AppendLine("Could not locate the launcher executable."); return; }
            var updater = Path.Combine(Path.GetTempPath(), $"scp-control-update-{Guid.NewGuid():N}.cmd");
            var script = $"""
                @echo off
                setlocal
                title SCP Control Update
                :wait
                tasklist /FI "PID eq {Environment.ProcessId}" /NH 2>nul | findstr /R /C:"[ ]{Environment.ProcessId}[ ]" >nul
                if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)
                cd /d "{EscapeBatch(repositoryRoot)}"
                git.exe pull --ff-only
                if errorlevel 1 (echo Update failed. & pause & exit /b 1)
                start "" "{EscapeBatch(launcher)}"
                timeout /t 2 /nobreak >nul
                del "%~f0" >nul 2>nul
                """;
            File.WriteAllText(updater, script, new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"\"{updater}\"\"") { WorkingDirectory = repositoryRoot, UseShellExecute = true });
            Application.Exit();
        }
        catch (Exception ex) { AppendLine($"Could not start updater: {ex.Message}"); }
    }

    private string GetPanelUrl()
    {
        var env = Path.Combine(repositoryRoot, ".env");
        if (File.Exists(env))
        {
            var configured = File.ReadLines(env).FirstOrDefault(line => line.StartsWith("Panel__PublicUrl=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1].Trim();
            if (Uri.TryCreate(configured, UriKind.Absolute, out var uri)) return uri.ToString();
        }
        return "http://127.0.0.1:5080";
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void SetBusy(bool busy, string? label = null) { foreach (var button in taskButtons) button.Enabled = !busy; Cursor = busy ? Cursors.WaitCursor : Cursors.Default; if (label is not null) SetStatus(label, false); }
    private void SetStatus(string text, bool online) { if (InvokeRequired) { BeginInvoke(() => SetStatus(text, online)); return; } status.Text = $"●  {text}"; status.ForeColor = online ? Color.FromArgb(71, 209, 140) : Color.FromArgb(119, 128, 141); }
    private void AppendLine(string text) { if (InvokeRequired) { BeginInvoke(() => AppendLine(text)); return; } output.AppendText(text + Environment.NewLine); }
    private static string EscapeBatch(string value) => value.Replace("%", "%%").Replace("\"", "\"\"");
    private static string FindNpmPath() { var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd"); return File.Exists(path) ? path : "npm.cmd"; }
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null) { if (File.Exists(Path.Combine(directory.FullName, "ScpSlPanel.sln"))) return directory.FullName; directory = directory.Parent; }
        return Directory.GetCurrentDirectory();
    }
}
