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

Enter a notification channel ID to send crash, restart, bridge, threshold, and audit alerts
through the bot instead of a webhook. Give the bot View Channel and Send Messages permission in
that channel. Enable the Guild Members privileged intent to display linked members' Discord
avatars and guild roles. A Steam Web API key enables Steam display names, profile links, and
avatars in Player Database.

The bot provides `/scp status`, `/scp players`, `/scp start`, `/scp stop`, `/scp restart`, and
`/scp announce`. Status and player queries are read-only. Control commands require an allowed
role and are written to the audit log. Treat the bot token like a password.

Discord role access can also be configured with the visual per-server permission editor. The
global Player Database supports staff notes, warnings, watchlists, allowlists, risk scoring, and
managed Steam-to-Discord identity links. Invalid or duplicate CSV mappings are shown by the
identity health panel.

Discord sends are retried up to three times, including rate-limit delays, and the latest 1,000
delivery results are retained in Settings. Optional daily fleet reports use the configured UTC
hour. Technical, moderation, and audit messages may be routed to separate channels.

### Discord donor synchronization

Open **Donors & Badges** to map each Discord donor role to a server and
numeric donor tier. Discord roles are selected by name from a dropdown. Linked identities are read from that server's
`LabAPI\configs\<query-port>\PlayhousePlugin\DiscordLinks.csv`, and the bot updates
`PlayhousePlugin\Donators.csv` when it connects, every five minutes, or when **Sync now** is used.
When multiple donor roles match, the mapping with the highest priority wins. The Discord server
booster state is written to the third column, existing pet indices are preserved, and new donors
start with pet index `0`. Individual linked users can also receive custom badge text and an
SCP:SL badge color. Badges are resolved from panel settings and applied live by the LabAPI bridge;
the owner can delegate these features per server with the `donors.manage` and `badges.manage`
administrator permissions.
they do not add fields to or create companion files beside `Donators.csv`.

## Account security

Each panel account can enable TOTP two-factor authentication in Settings using an authenticator
application. Once enabled, enter the six-digit code on the login page. The same page can revoke
all active cookies for the account. Changing a password also invalidates existing sessions.

## SQLite storage

JSON remains the default storage provider. To use SQLite, add the following setting:

```json
{
  "Panel": {
    "StorageProvider": "sqlite"
  }
}
```

The first read of each collection imports the corresponding existing JSON file into
`data/panel.db`; the original JSON files are retained as a rollback backup. Keep the Data
Protection key directory alongside the database when moving an installation.

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
| `Panel__DiscordOAuth__ClientId` | Discord application ID used for staff account login |
| `Panel__DiscordOAuth__ClientSecret` | Discord OAuth2 client secret |

For local Windows and Docker deployments, copy `.env.example` to `.env` and put these
values there. `start.bat` loads that file automatically, and Compose passes the same file
to the container. The real `.env` is ignored by Git and must not be committed.

To enable linked Discord login, create an application in the Discord Developer Portal and
add this OAuth2 redirect URL:

```text
https://esbpanel.ezaiahhangout.com/api/auth/discord/callback
```

Set both Discord OAuth environment variables and restart the panel. Staff members can then
open **Settings → Security**, connect their Discord identity to their existing panel account,
and use **Continue with Discord** on later visits. Discord login never creates panel accounts
or changes their server permissions.

The `data` directory contains security-sensitive user, audit, server, ban, and schedule
records. Back it up and restrict filesystem access.

### Operational safeguards

- **Settings → System health** checks storage, disk capacity, Discord, bridges, and backup freshness.
- Panel recovery archives are created daily under `Panel__Backups__Path` (or `data/panel-backups`)
  and retained according to `Panel__Backups__Retention`. Set `Panel__Backups__EncryptionKey`
  to a base64 16, 24, or 32-byte key for AES-GCM encryption; retain an offline copy of that key.
- High-impact operations require a fresh password or TOTP confirmation and retry automatically
  after successful confirmation.
- **Update center → Run preflight** requires healthy infrastructure, a verified recent recovery
  archive, stopped game servers, and a production frontend build.
- First-run owners receive an onboarding checklist until domain, OAuth, server, bridge, and
  recovery setup are ready.
- Set `Panel__DiscordOAuth__RequireGuildMembership=true` to reject linked Discord identities
  that are not members of the configured Discord guild.
- Personal dashboard widgets, favorite servers, and notification read state sync with each
  panel account.

Run the automated verification suite with:

```powershell
dotnet test tests\ScpSlPanel.Api.Tests\ScpSlPanel.Api.Tests.csproj
cd src\scpsl-panel-web
npm.cmd run build
npm.cmd run test:smoke
```

## Architecture

`ScpSlPanel.Api` is the control plane and static-file host. `ServerManager` is the local
process adapter, `PanelHub` carries real-time events, `SchedulerService` runs automation,
and `JsonStore` provides atomic file persistence. `scpsl-panel-web` is the React/TypeScript
operator console.

The next infrastructure layer for multi-machine deployments is a small authenticated node
agent that implements the same server adapter over mutually authenticated HTTPS. Moving
persistence to PostgreSQL can be done behind the existing services without changing the UI.
