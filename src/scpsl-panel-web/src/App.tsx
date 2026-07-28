import { FormEvent, useCallback, useEffect, useRef, useState } from 'react'
import {
  Activity, ArrowLeft, Ban as BanIcon, CalendarClock, ChevronRight, CircleGauge, Command,
  FileCode2, FolderOpen, Gamepad2, History, LayoutDashboard, LogOut, Menu, Play, Plug, Save,
  Plus, RefreshCw, RotateCcw, Server as ServerIcon, Settings, Shield,
  Square, Terminal, Users, X,
} from 'lucide-react'
import { api, ApiError } from './api'
import type { AuditEntry, Ban, BridgeSetup, BridgeStatus, Overview, Player, Schedule, Server } from './types'

type Page = 'overview' | 'servers' | 'server' | 'bans' | 'schedules' | 'audit' | 'admins' | 'settings'
type ServerTab = 'overview' | 'console' | 'players' | 'plugins' | 'files'
type User = { username: string; role: string; serverIds: string[]; permissions: string[] }

const nav: { page: Page; label: string; icon: typeof LayoutDashboard }[] = [
  { page: 'overview', label: 'Overview', icon: LayoutDashboard },
  { page: 'servers', label: 'Servers', icon: ServerIcon },
  { page: 'bans', label: 'Ban Manager', icon: BanIcon },
  { page: 'schedules', label: 'Scheduler', icon: CalendarClock },
  { page: 'audit', label: 'Audit Log', icon: History },
  { page: 'admins', label: 'Admin Manager', icon: Shield },
  { page: 'settings', label: 'Settings', icon: Settings },
]

const fmtBytes = (bytes: number) => bytes ? `${(bytes / 1024 / 1024).toFixed(0)} MB` : '0 MB'
const fmtState = (state: unknown) => typeof state === 'string' ? state.toUpperCase() : 'UNKNOWN'
const topLevelPages = new Set<Page>(['overview', 'servers', 'bans', 'schedules', 'audit', 'admins', 'settings'])
const serverTabs = new Set<ServerTab>(['overview', 'console', 'players', 'plugins', 'files'])
const readRoute = () => {
  const parts = window.location.pathname.split('/').filter(Boolean).map(decodeURIComponent)
  if (parts.length >= 2 && serverTabs.has(parts[1] as ServerTab))
    return { page: 'server' as Page, serverId: parts[0], tab: parts[1] as ServerTab }
  if (parts[0] && topLevelPages.has(parts[0] as Page))
    return { page: parts[0] as Page, serverId: null, tab: 'overview' as ServerTab }
  return { page: 'overview' as Page, serverId: null, tab: 'overview' as ServerTab }
}
const topLevelPath = (page: Page) => page === 'overview' ? '/' : `/${page}`
const serverPath = (serverId: string, tab: ServerTab) => `/${encodeURIComponent(serverId)}/${tab}`
const copyText = async (value: string) => {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(value)
      return
    } catch { /* Plain HTTP and restricted browsers need the fallback below. */ }
  }
  const textarea = document.createElement('textarea')
  textarea.value = value
  textarea.style.position = 'fixed'
  textarea.style.opacity = '0'
  document.body.appendChild(textarea)
  textarea.focus()
  textarea.select()
  const copied = document.execCommand('copy')
  textarea.remove()
  if (!copied) throw new Error('Automatic copy is unavailable. Select and copy the configuration manually.')
}
const fmtAgo = (value: string) => {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000))
  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  return `${Math.floor(seconds / 86400)}d ago`
}

export function App() {
  const [user, setUser] = useState<User | null | undefined>(undefined)
  useEffect(() => { api<User>('/auth/me').then(setUser).catch(() => setUser(null)) }, [])
  if (user === undefined) return <Splash />
  if (!user) return <Login onLogin={setUser} />
  return <Panel user={user} onLogout={() => setUser(null)} />
}

function Splash() {
  return <main className="center"><div className="brand-mark"><Shield size={28}/></div><p className="muted">Securing facility access…</p></main>
}

function Login({ onLogin }: { onLogin: (user: User) => void }) {
  const [username, setUsername] = useState('admin')
  const [password, setPassword] = useState('change-me-now')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setBusy(true); setError('')
    try { onLogin(await api<User>('/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) })) }
    catch (e) { setError(e instanceof Error ? e.message : 'Login failed') }
    finally { setBusy(false) }
  }
  return <main className="login-shell">
    <section className="login-card">
      <div className="brand"><div className="brand-mark"><Shield size={26}/></div><div><strong>SCP CONTROL</strong><span>FACILITY ADMINISTRATION</span></div></div>
      <div className="login-copy"><span className="eyebrow">SECURE ACCESS</span><h1>Welcome back,<br/>Administrator.</h1><p>Authenticate to access server operations and facility controls.</p></div>
      <form onSubmit={submit}>
        <label>USERNAME<input value={username} onChange={e => setUsername(e.target.value)} autoComplete="username"/></label>
        <label>PASSWORD<input type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password"/></label>
        {error && <p className="error">{error}</p>}
        <button className="primary wide" disabled={busy}>{busy ? 'AUTHENTICATING…' : 'AUTHENTICATE'} <ChevronRight size={17}/></button>
      </form>
      <footer><span className="status-dot"/> SYSTEM OPERATIONAL <span>•</span> ENCRYPTED CONNECTION</footer>
    </section>
  </main>
}

function Panel({ user, onLogout }: { user: User; onLogout: () => void }) {
  const initialRoute = readRoute()
  const [page, setPage] = useState<Page>(initialRoute.page)
  const [overview, setOverview] = useState<Overview | null>(null)
  const [selected, setSelected] = useState<string | null>(initialRoute.serverId)
  const [serverTab, setServerTab] = useState<ServerTab>(initialRoute.tab)
  const [drawer, setDrawer] = useState(false)
  const [error, setError] = useState('')

  const load = useCallback(async () => {
    try {
      const data = await api<Overview>('/overview')
      setOverview(data)
      if (!selected && data.servers[0]) setSelected(data.servers[0].id)
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) onLogout()
      else setError(e instanceof Error ? e.message : 'Unable to load panel')
    }
  }, [onLogout, selected])
  useEffect(() => { load(); const timer = setInterval(load, 5000); return () => clearInterval(timer) }, [load])
  useEffect(() => {
    const onPopState = () => {
      const route = readRoute()
      setPage(route.page)
      setSelected(route.serverId)
      setServerTab(route.tab)
    }
    window.addEventListener('popstate', onPopState)
    return () => window.removeEventListener('popstate', onPopState)
  }, [])
  const servers = overview?.servers ?? []
  const visibleNav = user.role === 'Owner' ? nav : nav.filter(item => !['bans', 'schedules', 'audit', 'admins'].includes(item.page))
  const selectedServer = servers.find(server => server.id === selected)
  const navigatePage = (nextPage: Page) => {
    setPage(nextPage)
    window.history.pushState({}, '', topLevelPath(nextPage))
  }
  const openServer = (id: string, tab: ServerTab = 'overview') => {
    setSelected(id); setServerTab(tab); setPage('server')
    window.history.pushState({}, '', serverPath(id, tab))
  }
  const navigateServerTab = (tab: ServerTab) => {
    if (!selected) return
    setServerTab(tab)
    window.history.pushState({}, '', serverPath(selected, tab))
  }

  const logout = async () => { await api('/auth/logout', { method: 'POST' }).catch(() => {}); onLogout() }
  return <div className="app-shell">
    <aside className={drawer ? 'open' : ''}>
      <div className="brand"><div className="brand-mark"><Shield size={24}/></div><div><strong>SCP CONTROL</strong><span>ADMINISTRATION</span></div></div>
      <nav>{visibleNav.map(item => <button key={item.page} className={page === item.page ? 'active' : ''} onClick={() => { navigatePage(item.page); setDrawer(false) }}><item.icon size={18}/>{item.label}</button>)}</nav>
      <div className="aside-bottom"><div className="system-line"><span className="status-dot"/>System operational</div><div className="profile"><div className="avatar">{user.username.slice(0, 2).toUpperCase()}</div><div><strong>{user.username}</strong><span>{user.role}</span></div><button onClick={logout} title="Log out"><LogOut size={17}/></button></div></div>
    </aside>
    <main className="workspace">
      <header><button className="mobile-menu" onClick={() => setDrawer(!drawer)}>{drawer ? <X/> : <Menu/>}</button><div><span className="crumb">SCP CONTROL / </span>{page === 'server' ? selectedServer?.name.toUpperCase() ?? 'SERVER' : nav.find(x => x.page === page)?.label.toUpperCase()}</div><div className="header-right"><span className="live-pill"><span className="status-dot"/> LIVE</span><button className="icon-button" onClick={load}><RefreshCw size={17}/></button></div></header>
      {error && <div className="toast error">{error}<button onClick={() => setError('')}><X size={15}/></button></div>}
      <div className="content">
        {page === 'overview' && <OverviewPage data={overview} navigatePage={navigatePage} openServer={openServer}/>}
        {page === 'servers' && <ServersPage servers={servers} refresh={load} openServer={openServer} onError={setError}/>}
        {page === 'server' && <ServerWorkspace server={selectedServer} tab={serverTab} setTab={navigateServerTab} refresh={load} back={() => navigatePage('servers')} onError={setError}/>}
        {page === 'bans' && <BansPage onError={setError}/>}
        {page === 'schedules' && <SchedulesPage servers={servers} onError={setError}/>}
        {page === 'audit' && <AuditPage/>}
        {page === 'admins' && <AdminManagerPage user={user} servers={servers} onError={setError}/>}
        {page === 'settings' && <SettingsPage user={user} onError={setError}/>}
      </div>
    </main>
  </div>
}

function PageTitle({ eyebrow, title, children }: { eyebrow: string; title: string; children?: React.ReactNode }) {
  return <div className="page-title"><div><span className="eyebrow">{eyebrow}</span><h1>{title}</h1></div>{children && <div className="actions">{children}</div>}</div>
}

function OverviewPage({ data, navigatePage, openServer }: { data: Overview | null; navigatePage: (p: Page) => void; openServer: (id: string) => void }) {
  if (!data) return <Skeleton/>
  const cards = [
    { label: 'SERVERS ONLINE', value: `${data.serversOnline}/${data.serversTotal}`, sub: data.serversTotal ? 'Fleet availability' : 'Add your first server', icon: ServerIcon },
    { label: 'PLAYERS ONLINE', value: data.playersOnline, sub: 'Across all instances', icon: Users },
    { label: 'MEMORY IN USE', value: fmtBytes(data.memoryBytes), sub: 'Managed processes', icon: Activity },
    { label: 'FACILITY STATUS', value: data.serversOnline ? 'ACTIVE' : 'STANDBY', sub: 'Control plane healthy', icon: CircleGauge },
  ]
  return <>
    <PageTitle eyebrow="FACILITY COMMAND" title="Operations overview"><button className="primary" onClick={() => navigatePage('servers')}><Plus size={16}/> ADD SERVER</button></PageTitle>
    <section className="stat-grid">{cards.map(card => <article className="stat-card" key={card.label}><div className="card-top"><span>{card.label}</span><card.icon size={19}/></div><strong>{card.value}</strong><small>{card.sub}</small></article>)}</section>
    <section className="split-grid">
      <article className="panel">
        <div className="panel-head"><div><span className="eyebrow">INFRASTRUCTURE</span><h2>Server fleet</h2></div><button className="text-button" onClick={() => navigatePage('servers')}>VIEW ALL <ChevronRight size={15}/></button></div>
        {data.servers.length ? <div className="server-list">{data.servers.slice(0, 5).map(server =>
          <button className="server-row" key={server.id} onClick={() => openServer(server.id)}>
            <span className={`server-state ${server.state}`}><Gamepad2 size={19}/></span><span className="server-name"><strong>{server.name}</strong><small>PID {server.processId ?? '—'} • {server.players}/{server.maxPlayers || '—'} players</small></span>
            <span className={`state-label ${server.state}`}><span/> {server.state}</span><span>{server.cpuPercent}% CPU</span><span>{fmtBytes(server.memoryBytes)}</span><ChevronRight size={16}/>
          </button>)}</div> : <EmptyMini text="No servers configured yet."/>}
      </article>
      <article className="panel">
        <div className="panel-head"><div><span className="eyebrow">SECURITY RECORD</span><h2>Recent activity</h2></div><button className="text-button" onClick={() => navigatePage('audit')}>AUDIT LOG <ChevronRight size={15}/></button></div>
        {data.recentActivity.length ? <div className="activity-list">{data.recentActivity.slice(0, 6).map(entry => <div className="activity-row" key={entry.id}><div className="event-icon"><Command size={16}/></div><div><strong>{entry.action}</strong><p>{entry.actor} · {entry.target}</p></div><time>{fmtAgo(entry.at)}</time></div>)}</div> : <EmptyMini text="Activity will appear here."/>}
      </article>
    </section>
  </>
}

function ServersPage({ servers, refresh, openServer, onError }: { servers: Server[]; refresh: () => void; openServer: (id: string) => void; onError: (e: string) => void }) {
  const [modal, setModal] = useState(false)
  const [busyServer, setBusyServer] = useState<string | null>(null)
  const action = async (id: string, name: string) => {
    if (busyServer) return
    setBusyServer(id)
    try { await api(`/servers/${id}/${name}`, { method: 'POST' }); await refresh() }
    catch (e) { onError(e instanceof Error ? e.message : 'Action failed') }
    finally { setBusyServer(null) }
  }
  return <>
    <PageTitle eyebrow="INFRASTRUCTURE" title="Server fleet"><button className="primary" onClick={() => setModal(true)}><Plus size={16}/> REGISTER SERVER</button></PageTitle>
    <div className="server-cards">{servers.map(server => <article className="server-card" key={server.id}>
      <div className="server-card-head"><div className={`server-state ${server.state}`}><Gamepad2/></div><div><h2>{server.name}</h2><span className={`state-label ${server.state}`}><span/> {server.state}</span></div><button className="manage-button" onClick={() => openServer(server.id)}>MANAGE <ChevronRight size={15}/></button></div>
      <div className="metric-strip"><div><span>PROCESS</span><strong>{server.processId ?? '—'}</strong></div><div><span>CPU</span><strong>{server.cpuPercent}%</strong></div><div><span>MEMORY</span><strong>{fmtBytes(server.memoryBytes)}</strong></div><div><span>PLAYERS</span><strong>{server.players}/{server.maxPlayers || '—'}</strong></div></div>
      {server.lastError && <p className="error">{server.lastError}</p>}
      <div className="server-actions"><button disabled={busyServer === server.id || server.state === 'online'} onClick={() => action(server.id, 'start')}><Play size={15}/> START</button><button disabled={busyServer === server.id || server.state === 'offline'} onClick={() => action(server.id, 'restart')}><RotateCcw size={15}/> RESTART</button><button disabled={busyServer === server.id || server.state === 'offline'} className="danger" onClick={() => action(server.id, 'stop')}><Square size={14}/> STOP</button></div>
    </article>)}</div>
    {!servers.length && <EmptyPage icon={ServerIcon} title="No servers registered" text="Connect your first SCP:SL dedicated server to begin operations."><button className="primary" onClick={() => setModal(true)}><Plus size={16}/> REGISTER SERVER</button></EmptyPage>}
    {modal && <AddServerModal close={() => setModal(false)} saved={() => { setModal(false); refresh() }} onError={onError}/>}
  </>
}

function AddServerModal({ close, saved, onError }: { close: () => void; saved: () => void; onError: (e: string) => void }) {
  const [form, setForm] = useState({ name: '', executablePath: '', arguments: '', workingDirectory: '', queryPort: 7777, autoRestart: true, autoStart: false, updateCommand: '' })
  const submit = async (e: FormEvent) => {
    e.preventDefault()
    try { await api('/servers', { method: 'POST', body: JSON.stringify(form) }); saved() }
    catch (err) { onError(err instanceof Error ? err.message : 'Unable to add server') }
  }
  return <div className="modal-backdrop" onMouseDown={e => e.target === e.currentTarget && close()}><form className="modal" onSubmit={submit}><div className="modal-head"><div><span className="eyebrow">NEW INFRASTRUCTURE</span><h2>Register server</h2></div><button type="button" className="icon-button" onClick={close}><X/></button></div>
    <label>DISPLAY NAME<input required placeholder="Site-02 Primary" value={form.name} onChange={e => setForm({...form, name: e.target.value})}/></label>
    <label>SERVER EXECUTABLE<input required placeholder="C:\SCPServer\LocalAdmin.exe" value={form.executablePath} onChange={e => setForm({...form, executablePath: e.target.value})}/></label>
    <label>WORKING DIRECTORY <small>(optional)</small><input placeholder="Inferred from executable" value={form.workingDirectory} onChange={e => setForm({...form, workingDirectory: e.target.value})}/></label>
    <div className="form-row"><label>ARGUMENTS<input value={form.arguments} onChange={e => setForm({...form, arguments: e.target.value})}/></label><label>QUERY PORT<input type="number" value={form.queryPort} onChange={e => setForm({...form, queryPort: Number(e.target.value)})}/></label></div>
    <label className="check"><input type="checkbox" checked={form.autoRestart} onChange={e => setForm({...form, autoRestart: e.target.checked})}/><span>Automatically restart after a crash</span></label>
    <div className="modal-actions"><button type="button" onClick={close}>CANCEL</button><button className="primary">REGISTER SERVER</button></div>
  </form></div>
}

function ServerWorkspace({ server, tab, setTab, refresh, back, onError }: { server?: Server; tab: ServerTab; setTab: (tab: ServerTab) => void; refresh: () => void; back: () => void; onError: (e: string) => void }) {
  const [busy, setBusy] = useState(false)
  if (!server) return <EmptyPage icon={ServerIcon} title="Server not found" text="The selected server was removed or is no longer available."><button onClick={back}>BACK TO SERVERS</button></EmptyPage>
  const action = async (name: string) => {
    if (busy) return
    setBusy(true)
    try { await api(`/servers/${server.id}/${name}`, { method: 'POST' }); await refresh() }
    catch (error) { onError(error instanceof Error ? error.message : 'Server action failed') }
    finally { setBusy(false) }
  }
  const tabs: { id: ServerTab; label: string; icon: typeof LayoutDashboard }[] = [
    { id: 'overview', label: 'Overview', icon: LayoutDashboard },
    { id: 'console', label: 'Console', icon: Terminal },
    { id: 'players', label: 'Players', icon: Users },
    { id: 'plugins', label: 'Plugins', icon: Plug },
    { id: 'files', label: 'Files & Config', icon: FolderOpen },
  ]
  return <>
    <button className="back-button" onClick={back}><ArrowLeft size={15}/> ALL SERVERS</button>
    <section className="server-hero">
      <div className={`server-state ${server.state}`}><Gamepad2 size={25}/></div>
      <div><span className="eyebrow">MANAGED INSTANCE</span><h1>{server.name}</h1><span className={`state-label ${server.state}`}><span/> {fmtState(server.state)}</span></div>
      <div className="server-hero-actions">
        <button disabled={busy || server.state === 'online'} onClick={() => action('start')}><Play size={15}/> START</button>
        <button disabled={busy || server.state === 'offline'} onClick={() => action('restart')}><RotateCcw size={15}/> RESTART</button>
        <button disabled={busy || server.state === 'offline'} className="danger" onClick={() => action('stop')}><Square size={14}/> STOP</button>
      </div>
    </section>
    <div className="server-tabs">{tabs.map(item => <button key={item.id} className={tab === item.id ? 'active' : ''} onClick={() => setTab(item.id)}><item.icon size={16}/>{item.label}</button>)}</div>
    <div className="server-tab-content">
      {tab === 'overview' && <ServerOverview server={server} setTab={setTab}/>}
      {tab === 'console' && <ConsolePage servers={[server]} selected={server.id} setSelected={() => {}} onError={onError} embedded/>}
      {tab === 'players' && <ServerPlayers server={server} onError={onError}/>}
      {tab === 'plugins' && <PluginsPage servers={[server]} selected={server.id} setSelected={() => {}} onError={onError} embedded/>}
      {tab === 'files' && <ServerFiles server={server} onError={onError}/>}
    </div>
  </>
}

function ServerOverview({ server, setTab }: { server: Server; setTab: (tab: ServerTab) => void }) {
  const uptime = server.startedAt ? fmtAgo(server.startedAt).replace(' ago', '') : 'Not running'
  return <section className="server-overview-grid">
    <div className="stat-grid server-stats">
      <article className="stat-card"><div className="card-top"><span>PROCESS ID</span><Activity size={18}/></div><strong>{server.processId ?? '—'}</strong><small>{fmtState(server.state)}</small></article>
      <article className="stat-card"><div className="card-top"><span>PLAYERS</span><Users size={18}/></div><strong>{server.players}/{server.maxPlayers || '—'}</strong><small>Current population</small></article>
      <article className="stat-card"><div className="card-top"><span>CPU / MEMORY</span><CircleGauge size={18}/></div><strong>{server.cpuPercent}%</strong><small>{fmtBytes(server.memoryBytes)} memory</small></article>
      <article className="stat-card"><div className="card-top"><span>UPTIME</span><History size={18}/></div><strong>{uptime}</strong><small>{server.startedAt ? new Date(server.startedAt).toLocaleString() : 'Offline'}</small></article>
    </div>
    <div className="quick-grid">
      <button onClick={() => setTab('console')}><Terminal/><div><strong>Live console</strong><span>Watch output and run commands</span></div><ChevronRight/></button>
      <button onClick={() => setTab('players')}><Users/><div><strong>Players</strong><span>View connected players and moderation</span></div><ChevronRight/></button>
      <button onClick={() => setTab('plugins')}><Plug/><div><strong>Plugins</strong><span>Inspect EXILED and NWAPI assemblies</span></div><ChevronRight/></button>
      <button onClick={() => setTab('files')}><FileCode2/><div><strong>Files & configuration</strong><span>Open and edit files below the server directory</span></div><ChevronRight/></button>
    </div>
    {server.lastError && <div className="server-alert"><strong>LAST ERROR</strong><span>{server.lastError}</span></div>}
  </section>
}

function ServerPlayers({ server, onError }: { server: Server; onError: (error: string) => void }) {
  const [status, setStatus] = useState<BridgeStatus | null>(null)
  const [setup, setSetup] = useState<BridgeSetup | null>(null)
  const load = useCallback(async () => {
    try { setStatus(await api<BridgeStatus>(`/servers/${server.id}/players`)) }
    catch (error) { onError(error instanceof Error ? error.message : 'Unable to load players') }
  }, [server.id, onError])
  useEffect(() => {
    void load()
    api<BridgeSetup>(`/servers/${server.id}/bridge`).then(setSetup).catch(() => {})
    const timer = setInterval(load, 3000)
    return () => clearInterval(timer)
  }, [load, server.id])
  const moderate = async (player: Player, action: 'kick' | 'ban') => {
    const reason = window.prompt(`Reason to ${action} ${player.nickname}:`, `Removed by ${action === 'kick' ? 'administrator' : 'moderator'}`)
    if (reason === null) return
    const durationMinutes = action === 'ban' ? Number(window.prompt('Ban duration in minutes:', '60') ?? 0) : null
    if (action === 'ban' && (!durationMinutes || durationMinutes < 1)) return
    try {
      await api(`/servers/${server.id}/players/${player.id}/${action}`, {
        method: 'POST',
        body: JSON.stringify({ playerId: player.id, reason, durationMinutes }),
      })
    } catch (error) { onError(error instanceof Error ? error.message : `Unable to ${action} player`) }
  }
  if (status?.connected) return <section className="players-panel">
    <div className="bridge-banner connected"><div><span className="status-dot"/><strong>LABAPI BRIDGE CONNECTED</strong><small>v{status.bridgeVersion} · LabAPI {status.apiVersion} · heartbeat {status.lastSeenAt ? fmtAgo(status.lastSeenAt) : 'now'}</small></div><span>{status.players.length}/{status.maxPlayers || '—'} PLAYERS</span></div>
    <Table headers={['PLAYER','USER ID','ROLE','CONNECTED','ACTIONS']}>{status.players.map(player => <tr key={player.id}><td><strong>{player.nickname}</strong><small>Player #{player.id} · {player.ipAddress || 'Identity protected'}</small></td><td className="mono">{player.userId || 'Do Not Track'}</td><td><span className="tag">{player.role}</span></td><td>{fmtAgo(player.connectedAt)}</td><td><div className="row-actions"><button onClick={() => moderate(player, 'kick')}>KICK</button><button className="danger" onClick={() => moderate(player, 'ban')}>BAN</button></div></td></tr>)}</Table>
    {!status.players.length && <EmptyMini text="Bridge connected. No players are currently online."/>}
  </section>
  const config = setup ? `panel_url: "${window.location.origin}"\nserver_id: "${setup.serverId}"\ntoken: "${setup.token}"\nheartbeat_seconds: 5\nrespect_do_not_track: true` : ''
  return <section className="bridge-install">
    <div className="bridge-install-intro"><div className="empty-icon"><Users size={28}/></div><div><span className="eyebrow">LABAPI BRIDGE REQUIRED</span><h2>Connect live players for {server.name}</h2><p>The bridge makes outbound heartbeat requests to this panel. No inbound game-server port is required.</p></div></div>
    <ol className="install-steps">
      <li><span>1</span><div><strong>Build the LabAPI bridge</strong><p>Run <code>build-bridge.bat</code> and enter the server's <code>SCPSL_Data\Managed</code> path.</p></div></li>
      <li><span>2</span><div><strong>Install the plugin DLL</strong><p>Copy <code>ScpSlPanel.LabApiBridge.dll</code> into your LabAPI plugins folder, then start the SCP:SL server once.</p></div></li>
      <li><span>3</span><div><strong>Configure this server</strong><p>Open the generated <code>scp-control-bridge.yml</code> and replace its contents with:</p><div className="config-snippet"><pre>{config || 'Loading owner-only bridge token…'}</pre><button disabled={!config} onClick={() => copyText(config).catch(error => onError(error.message))}>COPY</button></div></div></li>
      <li><span>4</span><div><strong>Restart SCP:SL</strong><p>This page will switch to the live player table within a few seconds.</p></div></li>
    </ol>
    <div className="integration-checks"><span className={server.state === 'online' ? 'ok' : ''}><i/>{server.state === 'online' ? 'Server process online' : 'Start the server process'}</span><span className={status?.connected ? 'ok' : ''}><i/>{status?.lastSeenAt ? `Last heartbeat ${fmtAgo(status.lastSeenAt)}` : 'Waiting for first heartbeat'}</span></div>
  </section>
}

function ServerFiles({ server, onError }: { server: Server; onError: (error: string) => void }) {
  const [path, setPath] = useState('config_gameplay.txt')
  const [content, setContent] = useState('')
  const [loadedPath, setLoadedPath] = useState('')
  const [busy, setBusy] = useState(false)
  const open = async () => {
    setBusy(true)
    try {
      const response = await fetch(`/api/servers/${server.id}/files/${path.split('/').map(encodeURIComponent).join('/')}`, { credentials: 'include' })
      if (!response.ok) throw new Error(response.status === 404 ? 'File not found below the registered server directory.' : `Unable to open file (${response.status}).`)
      setContent(await response.text()); setLoadedPath(path)
    } catch (error) { onError(error instanceof Error ? error.message : 'Unable to open file') }
    finally { setBusy(false) }
  }
  const save = async () => {
    if (!loadedPath) return
    setBusy(true)
    try {
      await api(`/servers/${server.id}/files/${loadedPath.split('/').map(encodeURIComponent).join('/')}`, { method: 'PUT', body: JSON.stringify({ content }) })
    } catch (error) { onError(error instanceof Error ? error.message : 'Unable to save file') }
    finally { setBusy(false) }
  }
  return <section className="file-editor">
    <div className="file-toolbar"><div><label>PATH BELOW SERVER DIRECTORY<input value={path} onChange={event => setPath(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') void open() }}/></label></div><button onClick={open} disabled={!path || busy}><FolderOpen size={15}/> OPEN</button><button className="primary" onClick={save} disabled={!loadedPath || busy}><Save size={15}/> SAVE</button></div>
    <div className="file-context">{loadedPath ? `Editing ${loadedPath}` : 'Enter a relative configuration path, then choose Open.'}</div>
    <textarea className="code-editor" value={content} onChange={event => setContent(event.target.value)} spellCheck={false} placeholder="File contents will appear here…"/>
  </section>
}

function ConsolePage({ servers, selected, setSelected, onError, embedded = false }: { servers: Server[]; selected: string | null; setSelected: (id: string) => void; onError: (e: string) => void; embedded?: boolean }) {
  const [lines, setLines] = useState<{ at: string; stream: string; line: string }[]>([])
  const [command, setCommand] = useState('')
  const outputRef = useRef<HTMLDivElement>(null)
  const server = servers.find(x => x.id === selected)
  const submit = async (event: FormEvent) => {
    event.preventDefault(); if (!selected || !command.trim()) return
    try { await api(`/servers/${selected}/command`, { method: 'POST', body: JSON.stringify({ command }) }); setCommand('') }
    catch (e) { onError(e instanceof Error ? e.message : 'Command failed') }
  }
  useEffect(() => {
    if (!selected) return
    let disposed = false
    let connection: import('@microsoft/signalr').HubConnection | undefined
    import('@microsoft/signalr').then(({ HubConnectionBuilder, LogLevel }) => {
      if (disposed) return
      connection = new HubConnectionBuilder().withUrl('/hub/panel').withAutomaticReconnect().configureLogging(LogLevel.Warning).build()
      connection.on('ConsoleLine', line => setLines(old => [...old.slice(-499), line]))
      connection.on('ServerChanged', () => {})
      connection.on('BridgeChanged', () => {})
      connection.start().then(() => connection?.invoke('JoinServer', selected)).catch(() => {})
    })
    return () => { disposed = true; connection?.stop() }
  }, [selected])
  useEffect(() => {
    const output = outputRef.current
    if (output) output.scrollTop = output.scrollHeight
  }, [lines])
  return <>
    {!embedded && <PageTitle eyebrow="REAL-TIME OPERATIONS" title="Live console"><select value={selected ?? ''} onChange={e => { setSelected(e.target.value); setLines([]) }}><option value="">Select server</option>{servers.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select></PageTitle>}
    <section className="console-panel"><div className="console-toolbar"><div><span className={`status-dot ${server?.state !== 'online' ? 'off' : ''}`}/>{server?.name ?? 'NO SERVER SELECTED'} <small>{fmtState(server?.state)}</small></div><button onClick={() => setLines([])}>CLEAR</button></div>
      <div className="console-output" ref={outputRef}>{lines.length ? lines.map((line, i) => <div key={i} className={line.stream}><time>{new Date(line.at).toLocaleTimeString()}</time><span>{line.line}</span></div>) : <div className="console-placeholder"><Terminal size={28}/><span>Console output will stream here.</span></div>}</div>
      <form className="command-line" onSubmit={submit}><span>RA &gt;</span><input disabled={!server || server.state !== 'online'} value={command} onChange={e => setCommand(e.target.value)} placeholder={server?.state === 'online' ? 'Enter server command…' : 'Server is offline'}/><button disabled={!command.trim()}>EXECUTE</button></form>
    </section>
  </>
}

function BansPage({ onError }: { onError: (e: string) => void }) {
  const [bans, setBans] = useState<Ban[]>([])
  const [show, setShow] = useState(false)
  const load = () => api<Ban[]>('/bans').then(setBans).catch(e => onError(e.message))
  useEffect(() => { void load() }, [])
  return <><PageTitle eyebrow="MODERATION" title="Ban manager"><button className="primary" onClick={() => setShow(true)}><BanIcon size={16}/> ISSUE BAN</button></PageTitle>
    <Table headers={['TARGET','REASON','ISSUED BY','EXPIRES','STATUS','']}>{bans.map(b => <tr key={b.id}><td><strong>{b.displayName}</strong><small>{b.target}</small></td><td>{b.reason}</td><td>{b.issuedBy}</td><td>{b.expiresAt ? new Date(b.expiresAt).toLocaleString() : 'Permanent'}</td><td><span className={`tag ${b.revoked ? '' : 'red'}`}>{b.revoked ? 'REVOKED' : 'ACTIVE'}</span></td><td>{!b.revoked && <button className="text-button" onClick={async () => { await api(`/bans/${b.id}`, { method: 'DELETE' }); load() }}>REVOKE</button>}</td></tr>)}</Table>
    {!bans.length && <EmptyMini text="No bans on record."/>}
    {show && <BanModal close={() => setShow(false)} saved={() => { setShow(false); load() }} onError={onError}/>}
  </>
}

function BanModal({ close, saved, onError }: { close: () => void; saved: () => void; onError: (e: string) => void }) {
  const [target, setTarget] = useState(''); const [reason, setReason] = useState(''); const [duration, setDuration] = useState(0)
  return <div className="modal-backdrop"><form className="modal small" onSubmit={async e => { e.preventDefault(); try { await api('/bans', { method: 'POST', body: JSON.stringify({ playerId: target, reason, durationMinutes: duration || null }) }); saved() } catch (x) { onError(x instanceof Error ? x.message : 'Failed') } }}><div className="modal-head"><div><span className="eyebrow">MODERATION ACTION</span><h2>Issue ban</h2></div><button type="button" className="icon-button" onClick={close}><X/></button></div><label>USER ID / IP<input required value={target} onChange={e => setTarget(e.target.value)}/></label><label>REASON<textarea required value={reason} onChange={e => setReason(e.target.value)}/></label><label>DURATION IN MINUTES <small>(0 = permanent)</small><input type="number" min="0" value={duration} onChange={e => setDuration(Number(e.target.value))}/></label><div className="modal-actions"><button type="button" onClick={close}>CANCEL</button><button className="danger solid">ISSUE BAN</button></div></form></div>
}

function SchedulesPage({ servers, onError }: { servers: Server[]; onError: (e: string) => void }) {
  const [items, setItems] = useState<Schedule[]>([])
  const [form, setForm] = useState({ serverId: '', name: 'Nightly restart', cron: '0 4 * * *', action: 'restart', enabled: true })
  const load = () => api<Schedule[]>('/schedules').then(setItems).catch(e => onError(e.message))
  useEffect(() => { void load() }, [])
  return <><PageTitle eyebrow="AUTOMATION" title="Scheduler"/>
    <section className="split-grid schedule-grid"><form className="panel form-panel" onSubmit={async e => { e.preventDefault(); try { await api('/schedules', { method: 'POST', body: JSON.stringify(form) }); load() } catch (x) { onError(x instanceof Error ? x.message : 'Failed') } }}><div className="panel-head"><div><span className="eyebrow">NEW AUTOMATION</span><h2>Create schedule</h2></div></div><label>SERVER<select required value={form.serverId} onChange={e => setForm({...form, serverId: e.target.value})}><option value="">Choose instance</option>{servers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label><label>NAME<input value={form.name} onChange={e => setForm({...form, name: e.target.value})}/></label><div className="form-row"><label>CRON EXPRESSION<input value={form.cron} onChange={e => setForm({...form, cron: e.target.value})}/></label><label>ACTION<select value={form.action} onChange={e => setForm({...form, action: e.target.value})}><option>restart</option><option>start</option><option>stop</option></select></label></div><button className="primary">CREATE SCHEDULE</button></form>
    <article className="panel"><div className="panel-head"><div><span className="eyebrow">AUTOMATION QUEUE</span><h2>Active schedules</h2></div></div>{items.length ? <div className="schedule-list">{items.map(x => <div className="schedule-row" key={x.id}><div className="event-icon"><CalendarClock size={17}/></div><div><strong>{x.name}</strong><p>{x.cron} · {x.action}</p></div><span className="tag">{x.enabled ? 'ENABLED' : 'PAUSED'}</span><button className="icon-button" onClick={async () => { await api(`/schedules/${x.id}`, { method: 'DELETE' }); load() }}><X size={16}/></button></div>)}</div> : <EmptyMini text="No scheduled actions."/ >}</article></section>
  </>
}

function PluginsPage({ servers, selected, setSelected, onError, embedded = false }: { servers: Server[]; selected: string | null; setSelected: (id: string) => void; onError: (e: string) => void; embedded?: boolean }) {
  type Plugin = { name: string; version: string; framework: string; enabled: boolean; path: string; configPaths: string[] }
  const [plugins, setPlugins] = useState<Plugin[]>([])
  const [busy, setBusy] = useState('')
  const [config, setConfig] = useState<{ plugin: string; path: string; content: string } | null>(null)
  const load = useCallback(() => {
    if (selected) api<Plugin[]>(`/plugins/${selected}`).then(setPlugins).catch(e => onError(e.message))
  }, [selected])
  useEffect(() => { load() }, [load])
  const action = async (plugin: Plugin, name: 'load' | 'unload' | 'restart') => {
    if (!selected || !window.confirm(`${name.toUpperCase()} ${plugin.name}? This performs a clean game-server restart.`)) return
    setBusy(plugin.path)
    try {
      await api(`/plugins/${selected}/action`, { method: 'POST', body: JSON.stringify({ path: plugin.path, action: name }) })
      load()
    } catch (e) { onError(e instanceof Error ? e.message : 'Plugin action failed') }
    finally { setBusy('') }
  }
  const openConfig = async (plugin: Plugin, path = plugin.configPaths[0]) => {
    if (!selected || !path) return
    try {
      const value = await api<{ path: string; content: string }>(`/plugins/${selected}/config?path=${encodeURIComponent(path)}`)
      setConfig({ plugin: plugin.name, ...value })
    } catch (e) { onError(e instanceof Error ? e.message : 'Unable to open plugin configuration') }
  }
  const saveConfig = async () => {
    if (!selected || !config) return
    setBusy(config.path)
    try {
      await api(`/plugins/${selected}/config`, { method: 'PUT', body: JSON.stringify({ path: config.path, content: config.content }) })
    } catch (e) { onError(e instanceof Error ? e.message : 'Unable to save plugin configuration') }
    finally { setBusy('') }
  }
  return <>{!embedded && <PageTitle eyebrow="EXTENSIONS" title="Plugin inventory"><select value={selected ?? ''} onChange={e => setSelected(e.target.value)}><option value="">Select server</option>{servers.map(x => <option value={x.id} key={x.id}>{x.name}</option>)}</select></PageTitle>}
    <Table headers={['PLUGIN','FRAMEWORK','VERSION','STATUS','ACTIONS']}>{plugins.map(x => <tr key={x.path}><td><strong>{x.name}</strong><small className="mono">{x.path}</small></td><td><span className="tag">{x.framework}</span></td><td>{x.version}</td><td><span className={`tag ${x.enabled ? '' : 'red'}`}>{x.enabled ? 'LOADED' : 'UNLOADED'}</span></td><td><div className="row-actions"><button disabled={busy === x.path} onClick={() => action(x, x.enabled ? 'unload' : 'load')}>{x.enabled ? 'UNLOAD' : 'LOAD'}</button><button disabled={busy === x.path || !x.enabled} onClick={() => action(x, 'restart')}><RefreshCw size={11}/> RESTART</button><button disabled={!x.configPaths?.length} onClick={() => openConfig(x)}><FileCode2 size={11}/> CONFIG {x.configPaths?.length ? `(${x.configPaths.length})` : ''}</button></div></td></tr>)}</Table>{!plugins.length && <EmptyPage icon={Plug} title="No plugins detected" text="LabAPI, EXILED and NWAPI plugin folders are scanned automatically."/>}
    {config && <section className="plugin-config"><div className="plugin-config-head"><div><span className="eyebrow">PLUGIN CONFIGURATION</span><h2>{config.plugin}</h2><label>CONFIG FILE<select value={config.path} onChange={e => { const plugin = plugins.find(x => x.name === config.plugin); if (plugin) void openConfig(plugin, e.target.value) }}>{plugins.find(x => x.name === config.plugin)?.configPaths.map(path => <option key={path} value={path}>{path.split(/[\\/]/).pop()}</option>)}</select></label><small className="mono">{config.path}</small></div><button className="icon-button" onClick={() => setConfig(null)}><X size={16}/></button></div><textarea className="code-editor" value={config.content} onChange={e => setConfig({ ...config, content: e.target.value })} spellCheck={false}/><div className="plugin-config-actions"><span>Save changes, then restart the plugin to apply them.</span><button className="primary" disabled={busy === config.path} onClick={saveConfig}><Save size={14}/> SAVE CONFIG</button></div></section>}
  </>
}

function AuditPage() {
  const [entries, setEntries] = useState<AuditEntry[]>([])
  useEffect(() => { api<AuditEntry[]>('/audit?take=250').then(setEntries) }, [])
  return <><PageTitle eyebrow="SECURITY RECORD" title="Audit log"/><Table headers={['TIME','ACTOR','ACTION','TARGET','DETAIL']}>{entries.map(x => <tr key={x.id}><td>{new Date(x.at).toLocaleString()}</td><td><strong>{x.actor}</strong></td><td><span className="tag">{x.action}</span></td><td>{x.target}</td><td>{x.detail}</td></tr>)}</Table>{!entries.length && <EmptyMini text="No activity recorded."/>}</>
}

function AdminManagerPage({ user, servers, onError }: { user: User; servers: Server[]; onError: (e: string) => void }) {
  type Account = { id: string; username: string; role: string; enabled: boolean; serverIds: string[]; permissions: string[] }
  const permissionOptions = [
    ['view', 'View server'], ['server.start', 'Start server'], ['server.stop', 'Stop server'],
    ['server.restart', 'Restart server'], ['console', 'View console and execute commands'],
    ['players', 'View players'], ['players.manage', 'Kick and ban players'], ['plugins', 'View plugins'],
    ['plugins.manage', 'Load / unload / restart plugins'], ['config', 'Read and edit configuration'],
  ]
  const blank = { id: '', username: '', password: '', enabled: true, serverIds: [] as string[], permissions: ['view'] as string[] }
  const [accounts, setAccounts] = useState<Account[]>([])
  const [form, setForm] = useState(blank)
  const [showModal, setShowModal] = useState(false)
  const [busy, setBusy] = useState(false)
  const loadAccounts = () => { if (user.role === 'Owner') api<Account[]>('/users').then(setAccounts).catch(e => onError(e.message)) }
  useEffect(() => { loadAccounts() }, [])
  if (user.role !== 'Owner') return <><PageTitle eyebrow="ACCESS CONTROL" title="Admin Manager"/><section className="panel"><Shield size={22}/><h2>Owner access required</h2><p>Only the panel owner can manage administrator accounts.</p></section></>
  const toggle = (key: 'serverIds' | 'permissions', value: string) =>
    setForm({ ...form, [key]: form[key].includes(value) ? form[key].filter(x => x !== value) : [...form[key], value] })
  const edit = (account: Account) => {
    setForm({ id: account.id, username: account.username, password: '', enabled: account.enabled, serverIds: account.serverIds, permissions: account.permissions })
    setShowModal(true)
  }
  const add = () => { setForm(blank); setShowModal(true) }
  const applyPreset = (name: 'viewer' | 'moderator' | 'manager' | 'full') => {
    const values = name === 'viewer' ? ['view', 'players', 'plugins']
      : name === 'moderator' ? ['view', 'console', 'players', 'players.manage']
      : name === 'manager' ? ['view', 'server.start', 'server.stop', 'server.restart', 'console', 'players', 'players.manage', 'plugins']
      : permissionOptions.map(([value]) => value)
    setForm({ ...form, permissions: values })
  }
  const save = async (event: FormEvent) => {
    event.preventDefault(); setBusy(true)
    try {
      await api(form.id ? `/users/${form.id}` : '/users', { method: form.id ? 'PUT' : 'POST', body: JSON.stringify({ username: form.username, password: form.password || null, enabled: form.enabled, serverIds: form.serverIds, permissions: form.permissions }) })
      setForm(blank); setShowModal(false); loadAccounts()
    } catch (e) { onError(e instanceof Error ? e.message : 'Unable to save account') }
    finally { setBusy(false) }
  }
  const remove = async (account: Account) => {
    if (!confirm(`Delete ${account.username}? This cannot be undone.`)) return
    try { await api(`/users/${account.id}`, { method: 'DELETE' }); loadAccounts() }
    catch (e) { onError(e instanceof Error ? e.message : 'Unable to delete account') }
  }
  return <><PageTitle eyebrow="ACCESS CONTROL" title="Admin Manager"/>
    <section className="admin-manager-card"><div className="admin-manager-head"><div><h2>All Admins <span>({accounts.length})</span></h2><p>Manage panel accounts, server access, and operational permissions.</p></div><button className="primary" onClick={add}><Plus size={15}/> ADD ADMIN</button></div>
      <div className="admin-table-wrap"><table className="admin-table"><thead><tr><th>ADMIN</th><th>AUTH</th><th>SERVER ACCESS</th><th>PERMISSIONS</th><th>STATUS</th><th>ACTIONS</th></tr></thead><tbody>{accounts.map(account => <tr key={account.id}><td><div className="admin-identity"><div className="avatar">{account.username.slice(0,2).toUpperCase()}</div><div><strong>{account.username}</strong><small>{account.role}</small></div></div></td><td><span className="admin-auth" title="Password authentication">◆</span></td><td>{account.role === 'Owner' ? <span className="scope-all">ALL SERVERS</span> : <span>{account.serverIds.length} of {servers.length}</span>}</td><td>{account.role === 'Owner' ? <span className="scope-all">ALL PERMISSIONS</span> : <span>{account.permissions.length} permission{account.permissions.length === 1 ? '' : 's'}</span>}</td><td><span className={`tag ${account.enabled ? '' : 'red'}`}>{account.enabled ? 'ENABLED' : 'DISABLED'}</span></td><td><div className="admin-actions">{account.role === 'Owner' ? <span className="your-account">YOUR ACCOUNT</span> : <><button className="edit" onClick={() => edit(account)}>EDIT</button><button className="delete" onClick={() => remove(account)}>DELETE</button></>}</div></td></tr>)}</tbody></table></div>
    </section>
    {showModal && <div className="modal-backdrop"><form className="modal admin-modal" onSubmit={save}><div className="modal-head"><div><span className="eyebrow">{form.id ? 'EDIT ADMINISTRATOR' : 'NEW ADMINISTRATOR'}</span><h2>{form.id ? form.username : 'Add admin'}</h2><p>Choose exactly which servers and controls this account can access.</p></div><button type="button" className="icon-button" onClick={() => setShowModal(false)}><X size={18}/></button></div><div className="admin-modal-body"><div className="form-row"><label>USERNAME<input required value={form.username} onChange={e => setForm({...form, username:e.target.value})}/></label><label>{form.id ? 'NEW PASSWORD (OPTIONAL)' : 'PASSWORD'}<input required={!form.id} minLength={8} type="password" value={form.password} onChange={e => setForm({...form,password:e.target.value})}/></label></div><label className="check-row"><input type="checkbox" checked={form.enabled} onChange={e => setForm({...form,enabled:e.target.checked})}/> Account enabled</label><div className="preset-row"><span>PERMISSION PRESETS</span><button type="button" onClick={() => applyPreset('viewer')}>VIEWER</button><button type="button" onClick={() => applyPreset('moderator')}>MODERATOR</button><button type="button" onClick={() => applyPreset('manager')}>MANAGER</button><button type="button" onClick={() => applyPreset('full')}>FULL ACCESS</button></div><div className="admin-scope-grid"><section><span className="eyebrow">SERVER ACCESS</span>{servers.map(server => <label className="check-row" key={server.id}><input type="checkbox" checked={form.serverIds.includes(server.id)} onChange={() => toggle('serverIds',server.id)}/><span><strong>{server.name}</strong><small>{server.state}</small></span></label>)}</section><section><span className="eyebrow">PERMISSIONS</span>{permissionOptions.map(([value,label]) => <label className="check-row" key={value}><input type="checkbox" checked={form.permissions.includes(value)} onChange={() => toggle('permissions',value)}/>{label}</label>)}</section></div></div><div className="modal-actions"><button type="button" onClick={() => setShowModal(false)}>CANCEL</button><button className="primary" disabled={busy}><Save size={14}/> {busy ? 'SAVING…' : 'SAVE ADMIN'}</button></div></form></div>}
  </>
}

function SettingsPage({ user, onError }: { user: User; onError: (e: string) => void }) {
  const [password, setPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)
  const changePassword = async (event: FormEvent) => {
    event.preventDefault(); setSaved(false)
    if (password.length < 8) return onError('Password must be at least 8 characters.')
    if (password !== confirmPassword) return onError('The passwords do not match.')
    setBusy(true)
    try {
      await api('/users/me/password', { method: 'PUT', body: JSON.stringify({ username: user.username, password }) })
      setPassword(''); setConfirmPassword(''); setSaved(true)
    } catch (e) { onError(e instanceof Error ? e.message : 'Unable to change password') }
    finally { setBusy(false) }
  }
  return <><PageTitle eyebrow="SYSTEM" title="Panel settings"/><section className="settings-grid"><form className="panel" onSubmit={changePassword}><Shield size={22}/><h2>Change my password</h2><p>Signed in as <strong>{user.username}</strong>.</p><label>NEW PASSWORD<input type="password" minLength={8} required value={password} onChange={e => setPassword(e.target.value)} autoComplete="new-password"/></label><label>CONFIRM PASSWORD<input type="password" minLength={8} required value={confirmPassword} onChange={e => setConfirmPassword(e.target.value)} autoComplete="new-password"/></label>{saved && <p className="success-message">Password changed successfully.</p>}<button className="primary wide" disabled={busy}>{busy ? 'SAVING…' : 'CHANGE PASSWORD'}</button></form><article className="panel"><FileCode2 size={22}/><h2>Configuration</h2><p>Panel settings live in <code>appsettings.json</code>. Environment variables can override production values.</p></article><article className="panel"><Activity size={22}/><h2>Access control</h2><p>Administrators and their server permissions are managed in the dedicated Admin Manager tab.</p></article></section></>
}

function Table({ headers, children }: { headers: string[]; children: React.ReactNode }) {
  return <div className="table-wrap"><table><thead><tr>{headers.map(x => <th key={x}>{x}</th>)}</tr></thead><tbody>{children}</tbody></table></div>
}

function EmptyPage({ icon: Icon, title, text, children }: { icon: typeof Users; title: string; text: string; children?: React.ReactNode }) {
  return <section className="empty-page"><div className="empty-icon"><Icon size={28}/></div><h2>{title}</h2><p>{text}</p>{children}</section>
}
function EmptyMini({ text }: { text: string }) { return <div className="empty-mini">{text}</div> }
function Skeleton() { return <div className="skeleton"><div/><div/><div/><div/></div> }
