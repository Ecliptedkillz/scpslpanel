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
- Read and write files below each registered server directory with path traversal protection
- Record and revoke timed or permanent bans
- Run start, stop, restart, or console-command schedules with five-field cron expressions
- Detect EXILED and NWAPI plugin assemblies
- Receive live players and roles through the included LabAPI bridge
- Cookie authentication, PBKDF2 password hashing, owner/admin roles, and user creation API
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
