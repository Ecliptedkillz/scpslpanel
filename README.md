# SCP Control

A self-hosted SCP: Secret Laboratory administration panel inspired by txAdmin. It combines
server process control, live consoles, moderation records, schedules, plugin discovery,
configuration access, metrics, authentication, and audit logging in one dashboard.

## Current capabilities

- Register and manage multiple local SCP:SL server processes
- Start, gracefully stop, force-stop, restart, crash-detect, and auto-restart
- Stream stdout/stderr to authenticated browsers through SignalR
- Execute LocalAdmin/console commands with a complete audit trail
- Monitor process ID, state, CPU, memory, and uptime
- Store seven days of CPU, memory, player-count, process-state, and bridge telemetry
- Record crash, bridge-disconnect, scheduled-restart, and restart-failure incidents
- Persist console output with search, pause/resume, command history, and log downloads
- Read and write files below each registered server directory with path traversal protection
- Browse and edit each server's live per-port SCP:SL configuration directory
- Record and revoke timed or permanent bans
- Run start, stop, restart, or console-command schedules with five-field cron expressions
- Run warned restart countdowns with in-game announcements and cancellation
- Detect EXILED and NWAPI plugin assemblies
- Receive live players and roles through the included LabAPI bridge
- Maintain a player database with name history, playtime, connections, notes, warnings,
  watchlist/allowlist records, mutes, kicks, and bans
- Create downloadable configuration backups and execute owner-configured update commands
- Send Discord crash, restart, bridge, and optional administrator-action notifications
- Cookie authentication, PBKDF2 password hashing, per-server access, and granular permissions
- Responsive React operator interface and a single deployable ASP.NET Core application

Player telemetry is intentionally adapter-based. SCP:SL does not expose all live player data
through child process streams alone. The included LabAPI bridge sends authenticated player
heartbeats to the panel and enables per-player kick and ban actions.

## Run locally

Requirements: .NET 9 SDK and Node.js 22 or later.

On Windows, run `build.bat` once to build the frontend and backend, then use `start.bat`
to listen on port 5080 on every network interface.

Repository helpers are also included:

- `git-update.bat` reviews local changes, asks for a commit message, and pushes the current branch.
- `git-pull.bat` safely pulls the current branch with fast-forward-only behavior.

```powershell
cd src\scpsl-panel-web
npm install
npm run build

cd ..\ScpSlPanel.Api
dotnet run --urls http://localhost:5080
```

Open `http://localhost:5080`. The development bootstrap credentials are:

- Username: `admin`
- Password: `change-me-now`

Change the password before exposing the application. Override it through
`Panel__BootstrapPassword`; bootstrap settings only apply when the user store is empty.

For frontend hot reload, run `npm run dev` in `src/scpsl-panel-web` while the API listens on
port 5080. Vite proxies API and SignalR traffic.

## Register SCP:SL

Use **Servers → Register server** and provide the executable that accepts console input and
emits server output. On Windows this is commonly the LocalAdmin host used to launch the
dedicated server. The working directory defaults to the executable's parent directory.

The service account running SCP Control must have permission to:

- execute the server binary;
- read/write only the server configuration directories you intend to expose;
- terminate its child process tree.

Do not run the panel as a system administrator/root account.

## Install the LabAPI game-server bridge

LabAPI is already bundled with current SCP:SL dedicated server builds. The bridge is compiled
against the copy installed with your game server so its required API version always matches.

1. Build and restart SCP Control, then open the registered server's **Players** tab.
2. Run `install-bridge.bat`.
3. Enter the full path to the server's `SCPSL_Data\Managed` directory. It must contain
   `LabApi.dll` and `Assembly-CSharp.dll`.
4. Enter the SCP:SL server port, panel URL, Server ID, and token shown in the Players tab.
5. Restart the SCP:SL server. The Players tab should report **LabAPI Bridge Connected** within
   a few seconds.

The installer builds against the server's LabAPI version, copies the plugin into
`%AppData%\SCP Secret Laboratory\LabAPI\plugins\<server-port>\`, and writes its YAML under
`configs\<server-port>\SCP Control Bridge`. Use `build-bridge.bat` instead if you want to
compile the DLL without installing it.

If `hoster_policy.txt` contains `gamedir_for_configs: true`, replace `%AppData%` above with the
dedicated server's local `AppData` directory. LabAPI's default plugin search paths are `global`
and the current server port.

The bridge makes outbound HTTP requests to the panel. `PanelUrl` must therefore be reachable
from the game-server machine; do not use `127.0.0.1` when the panel runs on another computer.
Use HTTPS when traffic leaves a trusted private network. Treat the bridge token like a password.

## Per-server pages

Every server has stable, bookmarkable routes:

- `/{server-id}/overview`
- `/{server-id}/monitoring`
- `/{server-id}/console`
- `/{server-id}/players`
- `/{server-id}/player-history`
- `/{server-id}/restarts`
- `/{server-id}/plugins`
- `/{server-id}/files`
- `/{server-id}/maintenance`

Monitoring samples are retained for seven days. Console logs and configuration backups are
stored below the panel data directory. Player records begin accumulating when the LabAPI bridge
sends heartbeats.

When `LabAPI\configs\<query-port>\PlayhousePlugin\DiscordLinks.csv` exists, Player Database
profiles are enriched with its Steam ID to Discord ID mappings. The file is read live and is
not copied into panel storage.

The maintenance updater only runs while the game server is offline. It creates a configuration
backup first, then executes the update command entered when registering the server. Treat update
commands as trusted owner-only configuration.

The **Files & Config** tab includes the registered working directory and the live SCP:SL
configuration directory at `%AppData%\SCP Secret Laboratory\config\<query-port>` on Windows.
The panel resolves `%AppData%` for the Windows account running the panel service. Grant
`config.view` and `config.write` independently for each registered server.

Configure Discord alerts under **Settings → Discord notifications**. Webhook URLs are sensitive
credentials and should not be shared.

## Discord bot

Owners can enable the embedded bot under **Settings → Discord bot**. Create an application in
the Discord Developer Portal, add a bot, and invite it with the `bot` and
`applications.commands` scopes. Enter its token, the guild ID, and comma-separated Discord role
IDs allowed to control servers. Discord administrators are always allowed.

The bot provides `/scp status`, `/scp players`, `/scp start`, `/scp stop`, `/scp restart`, and
`/scp announce`. Status and player queries are read-only. Control commands require an allowed
role and are written to the audit log. Treat the bot token like a password.

## Production

Set a long random bootstrap password, terminate TLS with Caddy, nginx, or a trusted ingress,
and restrict the panel to your administration network or VPN.

```bash
cp .env.example .env
# edit .env
docker compose up -d --build
```

Containers can manage only executables and directories mounted into the container. Native
host process management is usually simpler when SCP:SL already runs directly on Windows.
Publish a native build with:

```powershell
dotnet publish src\ScpSlPanel.Api\ScpSlPanel.Api.csproj -c Release -o publish
```

## Configuration

Settings may be provided in `appsettings.json` or standard ASP.NET Core environment variables:

| Setting | Purpose |
| --- | --- |
| `Panel__Name` | Dashboard/facility name |
| `Panel__DataPath` | JSON persistence directory |
| `Panel__BootstrapUsername` | First owner username |
| `Panel__BootstrapPassword` | First owner password |
| `Panel__AllowedHosts__0` | Allowed development UI origin |

The `data` directory contains security-sensitive user, audit, server, ban, and schedule
records. Back it up and restrict filesystem access.

## Architecture

`ScpSlPanel.Api` is the control plane and static-file host. `ServerManager` is the local
process adapter, `PanelHub` carries real-time events, `SchedulerService` runs automation,
and `JsonStore` provides atomic file persistence. `scpsl-panel-web` is the React/TypeScript
operator console.

The next infrastructure layer for multi-machine deployments is a small authenticated node
agent that implements the same server adapter over mutually authenticated HTTPS. Moving
persistence to PostgreSQL can be done behind the existing services without changing the UI.
