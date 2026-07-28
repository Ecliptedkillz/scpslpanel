import { FormEvent, useCallback, useEffect, useRef, useState } from 'react'
import {
  Activity, ArrowLeft, Ban as BanIcon, Bot, CalendarClock, ChevronRight, CircleGauge, Command,
  FileCode2, FolderOpen, Gamepad2, History, LayoutDashboard, LogOut, Menu, Play, Plug, Save,
  Plus, RefreshCw, RotateCcw, Server as ServerIcon, Settings, Shield,
  Square, Sun, Moon, Terminal, Users, X,
} from 'lucide-react'
import { api, ApiError } from './api'
import { ServerConfigEditor } from './components/ServerConfigEditor'
import { PlayerHistoryView, type StoredPlayer } from './components/PlayerHistoryView'
import { GlobalPlayerDatabase } from './components/GlobalPlayerDatabase'
import type { AuditEntry, Ban, BridgeSetup, BridgeStatus, Overview, Player, Schedule, Server } from './types'

type Page = 'overview' | 'servers' | 'server' | 'players' | 'bans' | 'schedules' | 'audit' | 'admins' | 'settings'
type ServerTab = 'overview' | 'monitoring' | 'console' | 'players' | 'player-history' | 'activity' | 'restarts' | 'plugins' | 'files' | 'maintenance'
type ServerAccessGrant = { serverId: string; permissions: string[] }
type User = { username: string; role: string; serverIds: string[]; permissions: string[]; serverAccess?: ServerAccessGrant[] }
type ThemeMode = 'dark' | 'light' | 'system'
const hasServerPermission = (user: User, serverId: string, permission: string) => {
  if (user.role === 'Owner') return true
  const permissions = user.serverAccess?.find(grant => grant.serverId === serverId)?.permissions ?? user.permissions
  return permissions.includes(permission)
}

const nav: { page: Page; label: string; icon: typeof LayoutDashboard }[] = [
  { page: 'overview', label: 'Overview', icon: LayoutDashboard },
  { page: 'servers', label: 'Servers', icon: ServerIcon },
  { page: 'players', label: 'Player Database', icon: Users },
  { page: 'bans', label: 'Ban Manager', icon: BanIcon },
  { page: 'schedules', label: 'Scheduler', icon: CalendarClock },
  { page: 'audit', label: 'Audit Log', icon: History },
  { page: 'admins', label: 'Admin Manager', icon: Shield },
  { page: 'settings', label: 'Settings', icon: Settings },
]

const fmtBytes = (bytes: number) => bytes ? `${(bytes / 1024 / 1024).toFixed(0)} MB` : '0 MB'
const fmtState = (state: unknown) => typeof state === 'string' ? state.toUpperCase() : 'UNKNOWN'
const topLevelPages = new Set<Page>(['overview', 'servers', 'players', 'bans', 'schedules', 'audit', 'admins', 'settings'])
const serverTabs = new Set<ServerTab>(['overview', 'monitoring', 'console', 'players', 'player-history', 'activity', 'restarts', 'plugins', 'files', 'maintenance'])
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
const formatPlaytime = (seconds: number) => seconds >= 3600
  ? `${Math.floor(seconds / 3600)}h ${Math.floor(seconds % 3600 / 60)}m`
  : `${Math.floor(seconds / 60)}m`

export function App() {
  const [theme, setTheme] = useState<ThemeMode>(() => (localStorage.getItem('scpcontrol-theme') as ThemeMode) || 'dark')
  const [user, setUser] = useState<User | null | undefined>(undefined)
  useEffect(() => {
    const apply = () => {
      const resolved = theme === 'system'
        ? (window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark') : theme
      document.documentElement.dataset.theme = resolved
      document.documentElement.style.colorScheme = resolved
      document.querySelector('meta[name="theme-color"]')?.setAttribute('content', resolved === 'light' ? '#ffffff' : '#090b0f')
    }
    apply()
    localStorage.setItem('scpcontrol-theme', theme)
    const media = window.matchMedia('(prefers-color-scheme: light)')
    media.addEventListener('change', apply)
    return () => media.removeEventListener('change', apply)
  }, [theme])
  useEffect(() => { api<User>('/auth/me').then(setUser).catch(() => setUser(null)) }, [])
  if (user === undefined) return <Splash />
  if (!user) return <Login onLogin={setUser} theme={theme} setTheme={setTheme}/>
  return <Panel user={user} onLogout={() => setUser(null)} theme={theme} setTheme={setTheme}/>
}

function Splash() {
  return <main className="center"><div className="brand-mark"><img src="/scpcontrol.png" alt="SCP Control"/></div><p className="muted">Securing facility access…</p></main>
}

function ThemeButton({ theme, setTheme }: { theme: ThemeMode; setTheme: (theme: ThemeMode) => void }) {
  return <button className="theme-toggle" title={`Theme: ${theme}`} onClick={() => setTheme(theme === 'dark' ? 'light' : theme === 'light' ? 'system' : 'dark')}>{theme === 'dark' ? <Moon size={16}/> : theme === 'light' ? <Sun size={16}/> : <CircleGauge size={16}/>}<span>{theme.toUpperCase()}</span></button>
}

function Login({ onLogin, theme, setTheme }: { onLogin: (user: User) => void; theme: ThemeMode; setTheme: (theme: ThemeMode) => void }) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setBusy(true); setError('')
    try { onLogin(await api<User>('/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) })) }
    catch (e) { setError(e instanceof Error ? e.message : 'Login failed') }
    finally { setBusy(false) }
  }
  return <main className="login-shell"><div className="login-theme"><ThemeButton theme={theme} setTheme={setTheme}/></div>
    <section className="login-card">
      <div className="brand"><div className="brand-mark"><img src="/scpcontrol.png" alt="SCP Control"/></div><div><strong>SCP CONTROL</strong><span>FACILITY ADMINISTRATION</span></div></div>
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

function Panel({ user, onLogout, theme, setTheme }: { user: User; onLogout: () => void; theme: ThemeMode; setTheme: (theme: ThemeMode) => void }) {
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
  const visibleNav = user.role === 'Owner' ? nav : nav.filter(item =>
    !['bans', 'schedules', 'audit', 'admins'].includes(item.page)
    && (item.page !== 'players' || user.serverAccess?.some(grant => grant.permissions.includes('players.history'))
      || user.permissions.includes('players.history')))
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
      <div className="brand"><div className="brand-mark"><img src="/scpcontrol.png" alt="SCP Control"/></div><div><strong>SCP CONTROL</strong><span>ADMINISTRATION</span></div></div>
      <nav>{visibleNav.map(item => <button key={item.page} className={page === item.page ? 'active' : ''} onClick={() => { navigatePage(item.page); setDrawer(false) }}><item.icon size={18}/>{item.label}</button>)}</nav>
      <div className="aside-bottom"><div className="system-line"><span className="status-dot"/>System operational</div><div className="profile"><div className="avatar">{user.username.slice(0, 2).toUpperCase()}</div><div><strong>{user.username}</strong><span>{user.role}</span></div><button onClick={logout} title="Log out"><LogOut size={17}/></button></div></div>
    </aside>
    <main className="workspace">
      <header><button className="mobile-menu" onClick={() => setDrawer(!drawer)}>{drawer ? <X/> : <Menu/>}</button><div><span className="crumb">SCP CONTROL / </span>{page === 'server' ? selectedServer?.name.toUpperCase() ?? 'SERVER' : nav.find(x => x.page === page)?.label.toUpperCase()}</div><div className="header-right"><span className="live-pill"><span className="status-dot"/> LIVE</span><ThemeButton theme={theme} setTheme={setTheme}/><button className="icon-button" onClick={load}><RefreshCw size={17}/></button></div></header>
      {error && <div className="toast error">{error}<button onClick={() => setError('')}><X size={15}/></button></div>}
      <div className="content">
        {page === 'overview' && <OverviewPage data={overview} navigatePage={navigatePage} openServer={openServer}/>}
        {page === 'servers' && <ServersPage user={user} servers={servers} refresh={load} openServer={openServer} onError={setError}/>}
        {page === 'players' && <GlobalPlayerDatabase onError={setError}/>}
        {page === 'server' && <ServerWorkspace user={user} server={selectedServer} tab={serverTab} setTab={navigateServerTab} refresh={load} back={() => navigatePage('servers')} onError={setError}/>}
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

function ServersPage({ user, servers, refresh, openServer, onError }: { user: User; servers: Server[]; refresh: () => void; openServer: (id: string) => void; onError: (e: string) => void }) {
  const [modal, setModal] = useState(false)
  const [busyServer, setBusyServer] = useState<string | null>(null)
  const action = async (id: string, name: string) => {
    if (busyServer) return
    const server = servers.find(item => item.id === id)
    if (name === 'restart' && !window.confirm(`Restart ${server?.name ?? 'this server'}? Connected players may be disconnected.`)) return
    if (name === 'stop' && !window.confirm(`Stop ${server?.name ?? 'this server'}? The panel will request a graceful shutdown.`)) return
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
      <div className="server-actions">{hasServerPermission(user, server.id, 'server.start') && <button disabled={busyServer === server.id || server.state === 'online'} onClick={() => action(server.id, 'start')}><Play size={15}/> START</button>}{hasServerPermission(user, server.id, 'server.restart') && <button disabled={busyServer === server.id || server.state === 'offline'} onClick={() => action(server.id, 'restart')}><RotateCcw size={15}/> RESTART</button>}{hasServerPermission(user, server.id, 'server.stop') && <button disabled={busyServer === server.id || server.state === 'offline'} className="danger" onClick={() => action(server.id, 'stop')}><Square size={14}/> STOP</button>}</div>
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
    <label>UPDATE COMMAND <small>(optional)</small><input placeholder="steamcmd +login anonymous +app_update …" value={form.updateCommand} onChange={e => setForm({...form, updateCommand: e.target.value})}/></label>
    <div className="form-row"><label>ARGUMENTS<input value={form.arguments} onChange={e => setForm({...form, arguments: e.target.value})}/></label><label>QUERY PORT<input type="number" value={form.queryPort} onChange={e => setForm({...form, queryPort: Number(e.target.value)})}/></label></div>
    <label className="check"><input type="checkbox" checked={form.autoRestart} onChange={e => setForm({...form, autoRestart: e.target.checked})}/><span>Automatically restart after a crash</span></label>
    <div className="modal-actions"><button type="button" onClick={close}>CANCEL</button><button className="primary">REGISTER SERVER</button></div>
  </form></div>
}

function ServerWorkspace({ user, server, tab, setTab, refresh, back, onError }: { user: User; server?: Server; tab: ServerTab; setTab: (tab: ServerTab) => void; refresh: () => void; back: () => void; onError: (e: string) => void }) {
  const [busy, setBusy] = useState(false)
  if (!server) return <EmptyPage icon={ServerIcon} title="Server not found" text="The selected server was removed or is no longer available."><button onClick={back}>BACK TO SERVERS</button></EmptyPage>
  const action = async (name: string) => {
    if (busy) return
    if (name === 'restart' && !window.confirm(`Restart ${server.name}? Connected players may be disconnected.`)) return
    if (name === 'stop' && !window.confirm(`Stop ${server.name}? The panel will request a graceful shutdown.`)) return
    setBusy(true)
    try { await api(`/servers/${server.id}/${name}`, { method: 'POST' }); await refresh() }
    catch (error) { onError(error instanceof Error ? error.message : 'Server action failed') }
    finally { setBusy(false) }
  }
  const permissions = user.role === 'Owner' ? null : user.serverAccess?.find(x => x.serverId === server.id)?.permissions ?? user.permissions
  const allowed = (permission: string) => permissions === null || permissions.includes(permission)
  const tabs: { id: ServerTab; label: string; icon: typeof LayoutDashboard; permission: string }[] = [
    { id: 'overview', label: 'Overview', icon: LayoutDashboard, permission: 'view' },
    { id: 'monitoring', label: 'Monitoring', icon: Activity, permission: 'monitoring' },
    { id: 'console', label: 'Console', icon: Terminal, permission: 'console.view' },
    { id: 'players', label: 'Live Players', icon: Users, permission: 'players' },
    { id: 'player-history', label: 'Player Database', icon: History, permission: 'players.history' },
    { id: 'activity', label: 'Activity & Rounds', icon: Activity, permission: 'monitoring' },
    { id: 'restarts', label: 'Restarts', icon: RotateCcw, permission: 'server.restart' },
    { id: 'plugins', label: 'Plugins', icon: Plug, permission: 'plugins' },
    { id: 'files', label: 'Files & Config', icon: FolderOpen, permission: 'config.view' },
    { id: 'maintenance', label: 'Maintenance', icon: Settings, permission: 'maintenance' },
  ]
  return <>
    <button className="back-button" onClick={back}><ArrowLeft size={15}/> ALL SERVERS</button>
    <section className="server-hero">
      <div className={`server-state ${server.state}`}><Gamepad2 size={25}/></div>
      <div><span className="eyebrow">MANAGED INSTANCE</span><h1>{server.name}</h1><span className={`state-label ${server.state}`}><span/> {fmtState(server.state)}</span></div>
      <div className="server-hero-actions">
        {allowed('server.start') && <button disabled={busy || server.state === 'online'} onClick={() => action('start')}><Play size={15}/> START</button>}
        {allowed('server.restart') && <button disabled={busy || server.state === 'offline'} onClick={() => action('restart')}><RotateCcw size={15}/> RESTART</button>}
        {allowed('server.stop') && <button disabled={busy || server.state === 'offline'} className="danger" onClick={() => action('stop')}><Square size={14}/> STOP</button>}
      </div>
    </section>
    <div className="server-tabs">{tabs.filter(item => allowed(item.permission)).map(item => <button key={item.id} className={tab === item.id ? 'active' : ''} onClick={() => setTab(item.id)}><item.icon size={16}/>{item.label}</button>)}</div>
    <div className="server-tab-content">
      {tab === 'overview' && <ServerOverview server={server} setTab={setTab}/>}
      {tab === 'monitoring' && <MonitoringPage server={server} onError={onError}/>}
      {tab === 'console' && <ConsolePage servers={[server]} selected={server.id} setSelected={() => {}} onError={onError} canWrite={allowed('console.write')} embedded/>}
      {tab === 'players' && <ServerPlayers server={server} onError={onError} initialMode="live" moderation={{kick:allowed('players.kick'),mute:allowed('players.mute'),ban:allowed('players.ban')}} canAnnounce={allowed('announcements')}/>}
      {tab === 'player-history' && <ServerPlayers server={server} onError={onError} initialMode="history" moderation={{kick:allowed('players.kick'),mute:allowed('players.mute'),ban:allowed('players.ban')}} canAnnounce={allowed('announcements')}/>}
      {tab === 'activity' && <ServerActivityPage server={server} onError={onError}/>}
      {tab === 'restarts' && <RestartManagerPage server={server} onError={onError}/>}
      {tab === 'plugins' && <PluginsPage servers={[server]} selected={server.id} setSelected={() => {}} onError={onError} embedded/>}
      {tab === 'files' && <ServerConfigEditor serverId={server.id} canWrite={allowed('config.write')} onError={onError}/>}
      {tab === 'maintenance' && <MaintenancePage server={server} onError={onError}/>}
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

function MonitoringPage({ server, onError }: { server: Server; onError: (value: string) => void }) {
  type Metric = { at: string; cpuPercent: number; memoryBytes: number; players: number; state: string; bridgeConnected: boolean }
  type Incident = { id: string; at: string; type: string; message: string; exitCode: number | null }
  const [metrics, setMetrics] = useState<Metric[]>([])
  const [incidents, setIncidents] = useState<Incident[]>([])
  const [hours, setHours] = useState(24)
  const load = useCallback(() => Promise.all([
    api<Metric[]>(`/servers/${server.id}/metrics?hours=${hours}`),
    api<Incident[]>(`/servers/${server.id}/incidents`)
  ]).then(([samples, events]) => { setMetrics(samples); setIncidents(events) }).catch(e => onError(e.message)), [server.id, hours, onError])
  useEffect(() => { void load(); const timer = setInterval(load, 30000); return () => clearInterval(timer) }, [load])
  return <section><div className="section-toolbar"><div><span className="eyebrow">TELEMETRY</span><h2>Server monitoring</h2></div><select value={hours} onChange={e => setHours(Number(e.target.value))}><option value="1">Last hour</option><option value="6">Last 6 hours</option><option value="24">Last 24 hours</option><option value="168">Last 7 days</option></select></div><div className="chart-grid"><MetricChart title="CPU USAGE" values={metrics.map(x => x.cpuPercent)} suffix="%"/><MetricChart title="MEMORY" values={metrics.map(x => x.memoryBytes / 1024 / 1024)} suffix=" MB"/><MetricChart title="PLAYERS" values={metrics.map(x => x.players)} suffix=""/></div><article className="panel incident-panel"><div className="panel-head"><div><span className="eyebrow">DIAGNOSTICS</span><h2>Crash and restart history</h2></div></div>{incidents.length ? incidents.map(item => <div className="incident-row" key={item.id}><span className={`event-icon ${item.type.includes('failed') || item.type === 'crash' ? 'danger' : ''}`}><Activity size={15}/></span><div><strong>{item.type.replaceAll('-', ' ').toUpperCase()}</strong><p>{item.message}</p></div><time>{new Date(item.at).toLocaleString()}</time></div>) : <EmptyMini text="No incidents recorded."/ >}</article></section>
}

function MetricChart({ title, values, suffix }: { title: string; values: number[]; suffix: string }) {
  const width = 500, height = 130, max = Math.max(1, ...values)
  const points = values.map((value, index) => `${values.length === 1 ? width : index / (values.length - 1) * width},${height - value / max * (height - 15)}`).join(' ')
  const latest = values.at(-1) ?? 0
  return <article className="metric-chart"><div><span>{title}</span><strong>{latest.toFixed(title === 'PLAYERS' ? 0 : 1)}{suffix}</strong></div><svg viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none"><polyline points={points}/></svg><small>{values.length} samples · peak {max.toFixed(1)}{suffix}</small></article>
}

function RestartManagerPage({ server, onError }: { server: Server; onError: (value: string) => void }) {
  type Countdown = { serverId: string; dueAt: string; message: string; actor: string }
  const [countdown, setCountdown] = useState<Countdown | null>(null)
  const [seconds, setSeconds] = useState(300)
  const [message, setMessage] = useState('Scheduled server restart')
  const load = useCallback(() => api<Countdown | null>(`/servers/${server.id}/restart/countdown`).then(setCountdown).catch(e => onError(e.message)), [server.id, onError])
  useEffect(() => { void load(); const timer = setInterval(load, 5000); return () => clearInterval(timer) }, [load])
  return <section className="restart-layout"><article className="panel restart-status"><div className="panel-head"><div><span className="eyebrow">RESTART MANAGER</span><h2>{countdown ? 'Countdown active' : 'No restart scheduled'}</h2></div></div>{countdown ? <><div className="countdown-clock">{Math.max(0, Math.ceil((new Date(countdown.dueAt).getTime() - Date.now()) / 1000))}<small>SECONDS REMAINING</small></div><p>{countdown.message}</p><button className="danger solid" onClick={async () => { await api(`/servers/${server.id}/restart/countdown`, {method:'DELETE'}); load() }}>CANCEL RESTART</button></> : <EmptyMini text="Create a warned restart using the form."/ >}</article><form className="panel form-panel" onSubmit={async e => { e.preventDefault(); try { await api(`/servers/${server.id}/restart/countdown`, { method:'POST', body:JSON.stringify({seconds,message}) }); load() } catch(error) { onError(error instanceof Error ? error.message : 'Unable to schedule restart') } }}><div className="panel-head"><div><span className="eyebrow">NEW COUNTDOWN</span><h2>Schedule warned restart</h2></div></div><label>COUNTDOWN<select value={seconds} onChange={e => setSeconds(Number(e.target.value))}><option value="60">1 minute</option><option value="300">5 minutes</option><option value="600">10 minutes</option><option value="1800">30 minutes</option><option value="3600">1 hour</option></select></label><label>ANNOUNCEMENT<input value={message} onChange={e => setMessage(e.target.value)}/></label><p className="muted">Players receive automatic warnings as the restart approaches. You can cancel at any time.</p><button className="primary" disabled={!!countdown}><RotateCcw size={14}/> START COUNTDOWN</button></form></section>
}

function MaintenancePage({ server, onError }: { server: Server; onError: (value: string) => void }) {
  type Backup = { id: string; createdAt: string; fileName: string; sizeBytes: number; actor: string }
  const [backups, setBackups] = useState<Backup[]>([])
  const [busy, setBusy] = useState('')
  const load = useCallback(() => api<Backup[]>(`/servers/${server.id}/backups`).then(setBackups).catch(e => onError(e.message)), [server.id, onError])
  useEffect(() => { void load() }, [load])
  const run = async (action: 'backups' | 'update') => {
    if (action === 'update' && !window.confirm(`Run the configured update command for ${server.name}? A backup will be created first.`)) return
    setBusy(action)
    try { await api(`/servers/${server.id}/${action}`, {method:'POST'}); load() }
    catch(e) { onError(e instanceof Error ? e.message : `Unable to run ${action}`) }
    finally { setBusy('') }
  }
  return <section className="maintenance-layout"><article className="panel"><div className="panel-head"><div><span className="eyebrow">SAFE OPERATIONS</span><h2>Update and backup</h2></div></div><div className="maintenance-actions"><button onClick={() => run('backups')} disabled={!!busy}><Save/><div><strong>CREATE BACKUP</strong><span>Archive server configuration files</span></div></button><button onClick={() => run('update')} disabled={!!busy || server.state !== 'offline'}><RefreshCw/><div><strong>RUN UPDATE</strong><span>{server.state === 'offline' ? 'Backup, then execute update command' : 'Stop the server before updating'}</span></div></button></div></article><article className="panel"><div className="panel-head"><div><span className="eyebrow">RECOVERY</span><h2>Available backups</h2></div></div>{backups.map(item => <div className="backup-row" key={item.id}><FileCode2/><div><strong>{item.fileName}</strong><small>{new Date(item.createdAt).toLocaleString()} · {fmtBytes(item.sizeBytes)} · {item.actor}</small></div><a className="manage-button" href={`/api/servers/${server.id}/backups/${encodeURIComponent(item.fileName)}`}>DOWNLOAD</a></div>)}{!backups.length && <EmptyMini text="No backups created yet."/ >}</article></section>
}

function ServerPlayers({ server, onError, initialMode = 'live', moderation = {kick:true,mute:true,ban:true}, canAnnounce = true }: { server: Server; onError: (error: string) => void; initialMode?: 'live' | 'history'; moderation?: {kick:boolean;mute:boolean;ban:boolean}; canAnnounce?: boolean }) {
  const [status, setStatus] = useState<BridgeStatus | null>(null)
  const [setup, setSetup] = useState<BridgeSetup | null>(null)
  const [history, setHistory] = useState<StoredPlayer[]>([])
  const [profile, setProfile] = useState<StoredPlayer | null>(null)
  const [note, setNote] = useState('')
  const [announcement, setAnnouncement] = useState('')
  const load = useCallback(async () => {
    try { setStatus(await api<BridgeStatus>(`/servers/${server.id}/players`)) }
    catch (error) { onError(error instanceof Error ? error.message : 'Unable to load players') }
  }, [server.id, onError])
  useEffect(() => {
    if (initialMode !== 'live') return
    void load()
    api<BridgeSetup>(`/servers/${server.id}/bridge`).then(setSetup).catch(() => {})
    const timer = setInterval(load, 3000)
    return () => clearInterval(timer)
  }, [initialMode, load, server.id])
  const loadHistory = useCallback(() => api<StoredPlayer[]>(`/servers/${server.id}/player-history`).then(setHistory).catch(error => onError(error.message)), [server.id, onError])
  useEffect(() => { if (initialMode === 'history') void loadHistory() }, [initialMode, loadHistory])
  const moderate = async (player: Player, action: 'kick' | 'ban' | 'mute' | 'unmute') => {
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
  if (initialMode === 'history') return <PlayerHistoryView server={server} history={history} profile={profile} setProfile={setProfile} note={note} setNote={setNote} reload={loadHistory} onError={onError}/>
  if (status?.connected) return <section className="players-panel">
    <div className="bridge-banner connected"><div><span className="status-dot"/><strong>LABAPI BRIDGE CONNECTED</strong><small>v{status.bridgeVersion} · LabAPI {status.apiVersion} · heartbeat {status.lastSeenAt ? fmtAgo(status.lastSeenAt) : 'now'}</small></div><span>{status.players.length}/{status.maxPlayers || '—'} PLAYERS</span></div>
    {canAnnounce && <form className="announcement-bar" onSubmit={async event => { event.preventDefault(); if (!announcement.trim()) return; try { await api(`/servers/${server.id}/announcement`, {method:'POST', body:JSON.stringify({message:announcement,durationSeconds:10})}); setAnnouncement('') } catch(error) { onError(error instanceof Error ? error.message : 'Unable to send announcement') } }}><input value={announcement} onChange={e => setAnnouncement(e.target.value)} placeholder="Broadcast an announcement to every player…"/><button className="primary">ANNOUNCE</button></form>}
    <Table headers={['PLAYER','USER ID','ROLE','PING / SESSION','VOICE','ACTIONS']}>{status.players.map(player => <tr key={player.id}><td><strong>{player.nickname}</strong><small>Player #{player.id} · {player.ipAddress || 'Identity protected'}</small></td><td className="mono">{player.userId || 'Do Not Track'}</td><td><span className="tag">{player.role}</span></td><td><strong>{player.ping || '—'} ms</strong><small>{formatPlaytime(player.sessionSeconds)}</small></td><td><span className={`tag ${player.isMuted ? 'red' : ''}`}>{player.isMuted ? 'MUTED' : 'OPEN'}</span></td><td><div className="row-actions">{moderation.mute && <button onClick={() => moderate(player, player.isMuted ? 'unmute' : 'mute')}>{player.isMuted ? 'UNMUTE' : 'MUTE'}</button>}{moderation.kick && <button onClick={() => moderate(player, 'kick')}>KICK</button>}{moderation.ban && <button className="danger" onClick={() => moderate(player, 'ban')}>BAN</button>}</div></td></tr>)}</Table>
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

function ServerActivityPage({ server, onError }: { server: Server; onError: (error: string) => void }) {
  type Event = { id: string; at: string; type: string; displayName: string | null; userId: string | null; detail: string }
  type Round = { id: string; startedAt: string; endedAt: string | null; leadingTeam: string | null; durationSeconds: number | null }
  const [events, setEvents] = useState<Event[]>([])
  const [rounds, setRounds] = useState<Round[]>([])
  const load = useCallback(async () => {
    try { const [activity, history] = await Promise.all([api<Event[]>(`/servers/${server.id}/activity`), api<Round[]>(`/servers/${server.id}/rounds`)]); setEvents(activity); setRounds(history) }
    catch(error) { onError(error instanceof Error ? error.message : 'Unable to load activity') }
  }, [server.id, onError])
  useEffect(() => { void load(); const timer = setInterval(load, 5000); return () => clearInterval(timer) }, [load])
  return <section className="activity-round-grid"><article className="panel"><div className="panel-head"><div><span className="eyebrow">PLAYER EVENTS</span><h2>Join, leave & moderation</h2></div></div><Table headers={['TIME','EVENT','PLAYER','DETAIL']}>{events.map(item => <tr key={item.id}><td>{new Date(item.at).toLocaleString()}</td><td><span className={`tag ${['ban','kick','mute'].includes(item.type) ? 'red' : ''}`}>{item.type.toUpperCase()}</span></td><td><strong>{item.displayName || 'Server'}</strong><small>{item.userId || ''}</small></td><td>{item.detail || '—'}</td></tr>)}</Table>{!events.length && <EmptyMini text="Bridge events will appear here."/>}</article><article className="panel"><div className="panel-head"><div><span className="eyebrow">ROUND TIMELINE</span><h2>Round history</h2></div></div><Table headers={['STARTED','ENDED','DURATION','RESULT']}>{rounds.map(round => <tr key={round.id}><td>{new Date(round.startedAt).toLocaleString()}</td><td>{round.endedAt ? new Date(round.endedAt).toLocaleString() : 'IN PROGRESS'}</td><td>{round.durationSeconds == null ? '—' : formatPlaytime(round.durationSeconds)}</td><td>{round.leadingTeam || '—'}</td></tr>)}</Table>{!rounds.length && <EmptyMini text="Completed rounds will appear here."/>}</article></section>
}

function ConsolePage({ servers, selected, setSelected, onError, embedded = false, canWrite = true }: { servers: Server[]; selected: string | null; setSelected: (id: string) => void; onError: (e: string) => void; embedded?: boolean; canWrite?: boolean }) {
  const [lines, setLines] = useState<{ at: string; stream: string; line: string }[]>([])
  const [command, setCommand] = useState('')
  const [search, setSearch] = useState('')
  const [paused, setPaused] = useState(false)
  const [commandHistory, setCommandHistory] = useState<string[]>([])
  const [historyIndex, setHistoryIndex] = useState(-1)
  const outputRef = useRef<HTMLDivElement>(null)
  const pausedRef = useRef(false)
  const server = servers.find(x => x.id === selected)
  const submit = async (event: FormEvent) => {
    event.preventDefault(); if (!canWrite || !selected || !command.trim()) return
    try { await api(`/servers/${selected}/command`, { method: 'POST', body: JSON.stringify({ command }) }); setCommandHistory(old => [...old.slice(-49), command]); setHistoryIndex(-1); setCommand('') }
    catch (e) { onError(e instanceof Error ? e.message : 'Command failed') }
  }
  useEffect(() => {
    if (!selected) return
    api<{ at: string; stream: string; line: string }[]>(`/servers/${selected}/console/history?take=1000`).then(setLines).catch(() => {})
    let disposed = false
    let connection: import('@microsoft/signalr').HubConnection | undefined
    import('@microsoft/signalr').then(({ HubConnectionBuilder, LogLevel }) => {
      if (disposed) return
      connection = new HubConnectionBuilder().withUrl('/hub/panel').withAutomaticReconnect().configureLogging(LogLevel.Warning).build()
      connection.on('ConsoleLine', line => { if (!pausedRef.current) setLines(old => [...old.slice(-1999), line]) })
      connection.on('ServerChanged', () => {})
      connection.on('BridgeChanged', () => {})
      connection.start().then(() => connection?.invoke('JoinServer', selected)).catch(() => {})
    })
    return () => { disposed = true; connection?.stop() }
  }, [selected])
  useEffect(() => { pausedRef.current = paused }, [paused])
  useEffect(() => {
    const output = outputRef.current
    if (output) output.scrollTop = output.scrollHeight
  }, [lines])
  return <>
    {!canWrite && <div className="file-context">Read-only console access. Command execution is not permitted for this account.</div>}
    {!embedded && <PageTitle eyebrow="REAL-TIME OPERATIONS" title="Live console"><select value={selected ?? ''} onChange={e => { setSelected(e.target.value); setLines([]) }}><option value="">Select server</option>{servers.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select></PageTitle>}
    <section className="console-panel"><div className="console-toolbar"><div><span className={`status-dot ${server?.state !== 'online' ? 'off' : ''}`}/>{server?.name ?? 'NO SERVER SELECTED'} <small>{fmtState(server?.state)} · {lines.length} LINES</small></div><div className="console-tools"><input value={search} onChange={e => setSearch(e.target.value)} placeholder="Search logs…"/><button className={paused ? 'active' : ''} onClick={() => setPaused(!paused)}>{paused ? 'RESUME' : 'PAUSE'}</button><a href={selected ? `/api/servers/${selected}/console/download` : '#'}>DOWNLOAD</a><button onClick={() => setLines([])}>CLEAR</button></div></div>
      <div className="console-output" ref={outputRef}>{lines.length ? lines.filter(item => !search || item.line.toLowerCase().includes(search.toLowerCase())).map((line, i) => <div key={i} className={line.stream}><time>{new Date(line.at).toLocaleTimeString()}</time><span>{line.line}</span></div>) : <div className="console-placeholder"><Terminal size={28}/><span>Console output will stream here.</span></div>}</div>
      {canWrite && <form className="command-line" onSubmit={submit}><span>RA &gt;</span><input disabled={!server || server.state !== 'online'} value={command} onChange={e => setCommand(e.target.value)} onKeyDown={e => { if (e.key === 'ArrowUp' && commandHistory.length) { e.preventDefault(); const next = Math.min(commandHistory.length - 1, historyIndex + 1); setHistoryIndex(next); setCommand(commandHistory[commandHistory.length - 1 - next]) } else if (e.key === 'ArrowDown') { e.preventDefault(); const next = historyIndex - 1; setHistoryIndex(next); setCommand(next < 0 ? '' : commandHistory[commandHistory.length - 1 - next]) } }} placeholder={server?.state === 'online' ? 'Enter server command… (↑ for history)' : 'Server is offline'}/><button disabled={!command.trim()}>EXECUTE</button></form>}
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
  const [form, setForm] = useState({ serverId: '', name: 'Nightly restart', cron: '0 4 * * *', action: 'restart', enabled: true, warningSeconds: 300 })
  const load = () => api<Schedule[]>('/schedules').then(setItems).catch(e => onError(e.message))
  useEffect(() => { void load() }, [])
  return <><PageTitle eyebrow="AUTOMATION" title="Scheduler"/>
    <section className="split-grid schedule-grid"><form className="panel form-panel" onSubmit={async e => { e.preventDefault(); try { await api('/schedules', { method: 'POST', body: JSON.stringify(form) }); load() } catch (x) { onError(x instanceof Error ? x.message : 'Failed') } }}><div className="panel-head"><div><span className="eyebrow">NEW AUTOMATION</span><h2>Create schedule</h2></div></div><label>SERVER<select required value={form.serverId} onChange={e => setForm({...form, serverId: e.target.value})}><option value="">Choose instance</option>{servers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label><label>NAME<input value={form.name} onChange={e => setForm({...form, name:e.target.value})}/></label><div className="form-row"><label>CRON EXPRESSION<input value={form.cron} onChange={e => setForm({...form,cron:e.target.value})}/></label><label>ACTION<select value={form.action} onChange={e => setForm({...form,action:e.target.value})}><option>restart</option><option>start</option><option>stop</option></select></label></div>{form.action === 'restart' && <label>PLAYER WARNING COUNTDOWN<select value={form.warningSeconds} onChange={e => setForm({...form,warningSeconds:Number(e.target.value)})}><option value="0">No warning</option><option value="60">1 minute</option><option value="300">5 minutes</option><option value="600">10 minutes</option><option value="1800">30 minutes</option></select></label>}<button className="primary">CREATE SCHEDULE</button></form>
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
  type Account = { id: string; username: string; role: string; enabled: boolean; serverIds: string[]; permissions: string[]; serverAccess?: ServerAccessGrant[] }
  const permissionOptions = [
    ['view', 'View server'], ['server.start', 'Start server'], ['server.stop', 'Stop server'],
    ['server.restart', 'Restart server'], ['console.view', 'View console and download logs'],
    ['console.write', 'Execute console commands'],
    ['players', 'View live players'], ['players.history', 'View player database'],
    ['players.notes', 'Add player notes'], ['players.actions', 'Warnings, watchlist and allowlist'],
    ['players.mute', 'Mute and unmute players'], ['players.kick', 'Kick players'], ['players.ban', 'Ban players'],
    ['plugins', 'View plugins'], ['plugins.manage', 'Load / unload / restart plugins'],
    ['config.view', 'Read server and plugin configuration'], ['config.write', 'Edit server and plugin configuration'],
    ['monitoring', 'View monitoring and incidents'], ['announcements', 'Send remote announcements'],
    ['maintenance', 'Backups and server updates'],
  ]
  const blank = { id: '', username: '', password: '', enabled: true, serverIds: [] as string[], permissions: [] as string[], serverAccess: [] as ServerAccessGrant[] }
  const [accounts, setAccounts] = useState<Account[]>([])
  const [form, setForm] = useState(blank)
  const [showModal, setShowModal] = useState(false)
  const [scopeServer, setScopeServer] = useState('')
  const [busy, setBusy] = useState(false)
  const loadAccounts = () => { if (user.role === 'Owner') api<Account[]>('/users').then(setAccounts).catch(e => onError(e.message)) }
  useEffect(() => { loadAccounts() }, [])
  if (user.role !== 'Owner') return <><PageTitle eyebrow="ACCESS CONTROL" title="Admin Manager"/><section className="panel"><Shield size={22}/><h2>Owner access required</h2><p>Only the panel owner can manage administrator accounts.</p></section></>
  const toggleServer = (serverId: string) => {
    const exists = form.serverAccess.some(x => x.serverId === serverId)
    setForm({...form, serverIds: exists ? form.serverIds.filter(x => x !== serverId) : [...form.serverIds, serverId],
      serverAccess: exists ? form.serverAccess.filter(x => x.serverId !== serverId) : [...form.serverAccess, {serverId, permissions:['view']}]})
    if (!exists) setScopeServer(serverId)
  }
  const togglePermission = (value: string) => setForm({...form, serverAccess: form.serverAccess.map(grant =>
    grant.serverId !== scopeServer ? grant : {...grant, permissions:grant.permissions.includes(value) ? grant.permissions.filter(x => x !== value) : [...grant.permissions,value]} )})
  const migratePermissions = (permissions: string[]) => [...new Set(permissions.flatMap(permission => {
    if (permission === 'lifecycle') return ['server.start', 'server.stop', 'server.restart']
    if (permission === 'console') return ['console.view', 'console.write']
    if (permission === 'config') return ['config.view', 'config.write']
    if (permission === 'players.manage') return ['players.actions', 'players.mute', 'players.kick', 'players.ban']
    return [permission]
  }))]
  const edit = (account: Account) => {
    const sourceGrants = account.serverAccess?.length ? account.serverAccess : account.serverIds.map(serverId => ({serverId, permissions:account.permissions}))
    const grants = sourceGrants.map(grant => ({...grant, permissions:migratePermissions(grant.permissions)}))
    setForm({ id: account.id, username: account.username, password: '', enabled: account.enabled, serverIds: grants.map(x => x.serverId), permissions: [], serverAccess: grants })
    setScopeServer(grants[0]?.serverId || '')
    setShowModal(true)
  }
  const add = () => { setForm(blank); setScopeServer(''); setShowModal(true) }
  const applyPreset = (name: 'viewer' | 'moderator' | 'manager' | 'full') => {
    const values = name === 'viewer' ? ['view', 'players', 'plugins', 'config.view']
      : name === 'moderator' ? ['view', 'console.view', 'players', 'players.history', 'players.notes', 'players.actions', 'players.mute', 'players.kick']
      : name === 'manager' ? ['view', 'server.start', 'server.stop', 'server.restart', 'console.view', 'console.write', 'players', 'players.history', 'players.notes', 'players.actions', 'players.mute', 'players.kick', 'players.ban', 'plugins', 'config.view']
      : permissionOptions.map(([value]) => value)
    setForm({ ...form, serverAccess: form.serverAccess.map(grant => grant.serverId === scopeServer ? {...grant,permissions:values} : grant) })
  }
  const save = async (event: FormEvent) => {
    event.preventDefault(); setBusy(true)
    try {
      await api(form.id ? `/users/${form.id}` : '/users', { method: form.id ? 'PUT' : 'POST', body: JSON.stringify({ username: form.username, password: form.password || null, enabled: form.enabled, serverIds: form.serverAccess.map(x => x.serverId), permissions: [], serverAccess: form.serverAccess }) })
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
      <div className="admin-table-wrap"><table className="admin-table"><thead><tr><th>ADMIN</th><th>AUTH</th><th>SERVER ACCESS</th><th>PERMISSIONS</th><th>STATUS</th><th>ACTIONS</th></tr></thead><tbody>{accounts.map(account => { const grants = account.serverAccess?.length ? account.serverAccess : account.serverIds.map(serverId => ({serverId,permissions:account.permissions})); const permissionCount = new Set(grants.flatMap(x => x.permissions)).size; return <tr key={account.id}><td><div className="admin-identity"><div className="avatar">{account.username.slice(0,2).toUpperCase()}</div><div><strong>{account.username}</strong><small>{account.role}</small></div></div></td><td><span className="admin-auth" title="Password authentication">◆</span></td><td>{account.role === 'Owner' ? <span className="scope-all">ALL SERVERS</span> : <span>{grants.length} of {servers.length}</span>}</td><td>{account.role === 'Owner' ? <span className="scope-all">ALL PERMISSIONS</span> : <span>{permissionCount} permission type{permissionCount === 1 ? '' : 's'}</span>}</td><td><span className={`tag ${account.enabled ? '' : 'red'}`}>{account.enabled ? 'ENABLED' : 'DISABLED'}</span></td><td><div className="admin-actions">{account.role === 'Owner' ? <span className="your-account">YOUR ACCOUNT</span> : <><button className="edit" onClick={() => edit(account)}>EDIT</button><button className="delete" onClick={() => remove(account)}>DELETE</button></>}</div></td></tr>})}</tbody></table></div>
    </section>
    {showModal && <div className="modal-backdrop"><form className="modal admin-modal" onSubmit={save}><div className="modal-head"><div><span className="eyebrow">{form.id ? 'EDIT ADMINISTRATOR' : 'NEW ADMINISTRATOR'}</span><h2>{form.id ? form.username : 'Add admin'}</h2><p>Each assigned server has its own independent permission set.</p></div><button type="button" className="icon-button" onClick={() => setShowModal(false)}><X size={18}/></button></div><div className="admin-modal-body"><div className="form-row"><label>USERNAME<input required value={form.username} onChange={e => setForm({...form, username:e.target.value})}/></label><label>{form.id ? 'NEW PASSWORD (OPTIONAL)' : 'PASSWORD'}<input required={!form.id} minLength={8} type="password" value={form.password} onChange={e => setForm({...form,password:e.target.value})}/></label></div><label className="check-row"><input type="checkbox" checked={form.enabled} onChange={e => setForm({...form,enabled:e.target.checked})}/> Account enabled</label><div className="preset-row"><span>PRESET FOR SELECTED SERVER</span><button type="button" disabled={!scopeServer} onClick={() => applyPreset('viewer')}>VIEWER</button><button type="button" disabled={!scopeServer} onClick={() => applyPreset('moderator')}>MODERATOR</button><button type="button" disabled={!scopeServer} onClick={() => applyPreset('manager')}>MANAGER</button><button type="button" disabled={!scopeServer} onClick={() => applyPreset('full')}>FULL ACCESS</button></div><div className="admin-scope-grid"><section><span className="eyebrow">SERVER ACCESS</span>{servers.map(server => <div className={`server-grant-row ${scopeServer === server.id ? 'active' : ''}`} key={server.id}><label className="check-row"><input type="checkbox" checked={form.serverAccess.some(x => x.serverId === server.id)} onChange={() => toggleServer(server.id)}/><span><strong>{server.name}</strong><small>{server.state}</small></span></label>{form.serverAccess.some(x => x.serverId === server.id) && <button type="button" onClick={() => setScopeServer(server.id)}>EDIT PERMS</button>}</div>)}</section><section><span className="eyebrow">PERMISSIONS {scopeServer ? `· ${servers.find(x => x.id === scopeServer)?.name}` : ''}</span>{scopeServer ? permissionOptions.map(([value,label]) => <label className="check-row" key={value}><input type="checkbox" checked={form.serverAccess.find(x => x.serverId === scopeServer)?.permissions.includes(value) ?? false} onChange={() => togglePermission(value)}/>{label}</label>) : <EmptyMini text="Assign and select a server to configure its permissions."/>}</section></div></div><div className="modal-actions"><button type="button" onClick={() => setShowModal(false)}>CANCEL</button><button className="primary" disabled={busy}><Save size={14}/> {busy ? 'SAVING…' : 'SAVE ADMIN'}</button></div></form></div>}
  </>
}

type IntegrationSettings = {
  discordWebhookUrl: string; notifyCrash: boolean; notifyRestart: boolean; notifyBridgeOffline: boolean
  notifyAdminActions: boolean; notifyHighCpu: boolean; highCpuPercent: number
  notifyHighMemory: boolean; highMemoryMb: number; alertCooldownMinutes: number
  crashMessage: string; bridgeOfflineMessage: string; highCpuMessage: string
  highMemoryMessage: string; restartFailureMessage: string; scheduleFailureMessage: string
  discordBotEnabled: boolean; discordBotToken: string; discordGuildId: number; discordControlRoleIds: string
  discordNotificationChannelId: number; steamWebApiKey: string
}
const defaultIntegration: IntegrationSettings = {
  discordWebhookUrl: '', notifyCrash: true, notifyRestart: true, notifyBridgeOffline: true,
  notifyAdminActions: false, notifyHighCpu: true, highCpuPercent: 90,
  notifyHighMemory: true, highMemoryMb: 4096, alertCooldownMinutes: 15,
  crashMessage: '{server} stopped unexpectedly with exit code {exitCode}. Auto-restart is {autoRestart}.',
  bridgeOfflineMessage: '{server} is online, but its LabAPI bridge stopped responding.',
  highCpuMessage: '{server} CPU usage is {cpu}% (alert threshold: {threshold}%).',
  highMemoryMessage: '{server} memory usage is {memoryMb} MB (alert threshold: {thresholdMb} MB).',
  restartFailureMessage: '{server} failed to restart automatically: {error}',
  scheduleFailureMessage: "Schedule '{schedule}' failed for {server}: {error}",
  discordBotEnabled: false, discordBotToken: '', discordGuildId: 0, discordControlRoleIds: '',
  discordNotificationChannelId: 0, steamWebApiKey: '',
}

function SettingsPage({ user, onError }: { user: User; onError: (e: string) => void }) {
  return <><PageTitle eyebrow="SYSTEM" title="Panel settings"/><section className="settings-grid"><SettingsBasePage user={user} onError={onError}/>{user.role === 'Owner' && <DiscordBotPanel onError={onError}/>} {user.role === 'Owner' && <AlertRulesPanel onError={onError}/>}</section></>
}

function DiscordBotPanel({ onError }: { onError: (e: string) => void }) {
  const [settings,setSettings] = useState<IntegrationSettings | null>(null)
  const [status,setStatus] = useState<{enabled:boolean;connected:boolean;botName:string|null;error:string|null}|null>(null)
  const load = useCallback(() => Promise.all([
    api<IntegrationSettings>('/integrations').then(setSettings),
    api<typeof status>('/integrations/discord/bot/status').then(setStatus),
  ]).catch(e => onError(e.message)), [onError])
  useEffect(() => { void load(); const timer=setInterval(load,10000); return () => clearInterval(timer) }, [load])
  if (!settings) return null
  return <form className="panel settings-alerts discord-settings" onSubmit={async e => {
    e.preventDefault()
    try { await api('/integrations',{method:'PUT',body:JSON.stringify(settings)}); setTimeout(load,1200) }
    catch(error) { onError(error instanceof Error ? error.message : 'Unable to save Discord bot settings') }
  }}>
    <Bot size={26}/><h2>Discord bot</h2>
    <p><span className={`status-dot ${status?.connected ? '' : 'off'}`}/>{status?.connected ? `Connected as ${status.botName}` : status?.error || 'Not connected'}</p>
    <label className="check-row"><input type="checkbox" checked={settings.discordBotEnabled} onChange={e=>setSettings({...settings,discordBotEnabled:e.target.checked})}/> Enable embedded Discord bot</label>
    <div className="form-row"><label>BOT TOKEN<input type="password" value={settings.discordBotToken} onChange={e=>setSettings({...settings,discordBotToken:e.target.value})}/></label><label>GUILD ID<input value={settings.discordGuildId || ''} onChange={e=>setSettings({...settings,discordGuildId:Number(e.target.value)})}/></label><label>NOTIFICATION CHANNEL ID<input value={settings.discordNotificationChannelId || ''} onChange={e=>setSettings({...settings,discordNotificationChannelId:Number(e.target.value)})}/></label><label>CONTROL ROLE IDS<input value={settings.discordControlRoleIds} onChange={e=>setSettings({...settings,discordControlRoleIds:e.target.value})} placeholder="123…, 456…"/></label><label>STEAM WEB API KEY<input type="password" value={settings.steamWebApiKey} onChange={e=>setSettings({...settings,steamWebApiKey:e.target.value})}/></label></div>
    <p>Commands: <code>/scp status</code>, <code>players</code>, <code>start</code>, <code>stop</code>, <code>restart</code>, and <code>announce</code>. Control commands require Discord Administrator or one of the comma-separated role IDs.</p>
    <button className="primary">SAVE BOT SETTINGS</button>
  </form>
}

function AlertRulesPanel({ onError }: { onError: (e: string) => void }) {
  const [settings, setSettings] = useState<IntegrationSettings | null>(null)
  const [saved, setSaved] = useState(false)
  useEffect(() => { api<IntegrationSettings>('/integrations').then(setSettings).catch(e => onError(e.message)) }, [])
  if (!settings) return null
  const save = async (event: FormEvent) => {
    event.preventDefault(); setSaved(false)
    try {
      await api('/integrations', { method: 'PUT', body: JSON.stringify(settings) })
      setSaved(true)
    } catch (error) { onError(error instanceof Error ? error.message : 'Unable to save alert rules') }
  }
  return <form className="panel discord-settings settings-alerts" onSubmit={save}>
    <Activity size={22}/><h2>Alert rules and messages</h2>
    <p>Thresholds must be exceeded for two samples. Tokens such as <code>{'{server}'}</code>, <code>{'{cpu}'}</code>, and <code>{'{error}'}</code> are replaced when an alert is sent.</p>
    <div className="form-row"><label>CPU THRESHOLD (%)<input type="number" min={1} max={100} value={settings.highCpuPercent} onChange={e => setSettings({...settings, highCpuPercent:Number(e.target.value)})}/></label><label>MEMORY THRESHOLD (MB)<input type="number" min={128} value={settings.highMemoryMb} onChange={e => setSettings({...settings, highMemoryMb:Number(e.target.value)})}/></label><label>COOLDOWN (MINUTES)<input type="number" min={1} max={1440} value={settings.alertCooldownMinutes} onChange={e => setSettings({...settings, alertCooldownMinutes:Number(e.target.value)})}/></label></div>
    <label className="check-row"><input type="checkbox" checked={settings.notifyHighCpu} onChange={e => setSettings({...settings,notifyHighCpu:e.target.checked})}/> Alert on sustained high CPU</label>
    <label className="check-row"><input type="checkbox" checked={settings.notifyHighMemory} onChange={e => setSettings({...settings,notifyHighMemory:e.target.checked})}/> Alert on sustained high memory</label>
    <div className="settings-message-grid">
      <label>CRASH MESSAGE<textarea value={settings.crashMessage} onChange={e => setSettings({...settings,crashMessage:e.target.value})}/></label>
      <label>BRIDGE OFFLINE MESSAGE<textarea value={settings.bridgeOfflineMessage} onChange={e => setSettings({...settings,bridgeOfflineMessage:e.target.value})}/></label>
      <label>HIGH CPU MESSAGE<textarea value={settings.highCpuMessage} onChange={e => setSettings({...settings,highCpuMessage:e.target.value})}/></label>
      <label>HIGH MEMORY MESSAGE<textarea value={settings.highMemoryMessage} onChange={e => setSettings({...settings,highMemoryMessage:e.target.value})}/></label>
      <label>AUTO-RESTART FAILURE MESSAGE<textarea value={settings.restartFailureMessage} onChange={e => setSettings({...settings,restartFailureMessage:e.target.value})}/></label>
      <label>SCHEDULE FAILURE MESSAGE<textarea value={settings.scheduleFailureMessage} onChange={e => setSettings({...settings,scheduleFailureMessage:e.target.value})}/></label>
    </div>
    {saved && <p className="success-message">Alert rules saved.</p>}<button className="primary">SAVE ALERT RULES</button>
  </form>
}

function SettingsBasePage({ user, onError }: { user: User; onError: (e: string) => void }) {
  const [password, setPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)
  const [integration, setIntegration] = useState<IntegrationSettings>(defaultIntegration)
  useEffect(() => { if (user.role === 'Owner') api<typeof integration>('/integrations').then(setIntegration).catch(() => {}) }, [user.role])
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
  return <><form className="panel settings-password" onSubmit={changePassword}><Shield size={26}/><h2>Change my password</h2><p>Signed in as <strong>{user.username}</strong>.</p><label>NEW PASSWORD<input type="password" minLength={8} required value={password} onChange={e => setPassword(e.target.value)} autoComplete="new-password"/></label><label>CONFIRM PASSWORD<input type="password" minLength={8} required value={confirmPassword} onChange={e => setConfirmPassword(e.target.value)} autoComplete="new-password"/></label>{saved && <p className="success-message">Password changed successfully.</p>}<button className="primary wide" disabled={busy}>{busy ? 'SAVING…' : 'CHANGE PASSWORD'}</button></form>{user.role === 'Owner' && <form className="panel discord-settings settings-discord" onSubmit={async e => { e.preventDefault(); try { await api('/integrations',{method:'PUT',body:JSON.stringify(integration)}) } catch(error) { onError(error instanceof Error ? error.message : 'Unable to save Discord settings') } }}><Activity size={26}/><h2>Discord notifications</h2><p>Send crash, restart, and administrative alerts to a Discord channel.</p><label>WEBHOOK URL<input type="password" value={integration.discordWebhookUrl} onChange={e => setIntegration({...integration,discordWebhookUrl:e.target.value})} placeholder="https://discord.com/api/webhooks/…"/></label><label className="check-row"><input type="checkbox" checked={integration.notifyCrash} onChange={e => setIntegration({...integration,notifyCrash:e.target.checked})}/> Crash alerts</label><label className="check-row"><input type="checkbox" checked={integration.notifyRestart} onChange={e => setIntegration({...integration,notifyRestart:e.target.checked})}/> Restart alerts</label><label className="check-row"><input type="checkbox" checked={integration.notifyAdminActions} onChange={e => setIntegration({...integration,notifyAdminActions:e.target.checked})}/> Admin action log</label><div className="discord-actions"><button type="button" onClick={() => api('/integrations/discord/test',{method:'POST'}).catch(e => onError(e.message))}>TEST</button><button className="primary">SAVE</button></div></form>}{user.role === 'Owner' && <article className="panel settings-config"><FileCode2 size={26}/><h2>Configuration</h2><p>Panel settings live in <code>appsettings.json</code>. Administrators and server permissions are managed in Admin Manager.</p></article>}</>
}

function Table({ headers, children }: { headers: string[]; children: React.ReactNode }) {
  return <div className="table-wrap"><table><thead><tr>{headers.map(x => <th key={x}>{x}</th>)}</tr></thead><tbody>{children}</tbody></table></div>
}

function EmptyPage({ icon: Icon, title, text, children }: { icon: typeof Users; title: string; text: string; children?: React.ReactNode }) {
  return <section className="empty-page"><div className="empty-icon"><Icon size={28}/></div><h2>{title}</h2><p>{text}</p>{children}</section>
}
function EmptyMini({ text }: { text: string }) { return <div className="empty-mini">{text}</div> }
function Skeleton() { return <div className="skeleton"><div/><div/><div/><div/></div> }
