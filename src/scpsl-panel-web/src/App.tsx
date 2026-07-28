import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react'
import {
  Activity, Ban as BanIcon, CalendarClock, ChevronRight, CircleGauge, Command,
  FileCode2, Gamepad2, History, LayoutDashboard, LogOut, Menu, Play, Plug,
  Plus, RefreshCw, RotateCcw, Server as ServerIcon, Settings, Shield,
  Square, Terminal, Users, X,
} from 'lucide-react'
import { api, ApiError } from './api'
import type { AuditEntry, Ban, Overview, Schedule, Server } from './types'

type Page = 'overview' | 'servers' | 'console' | 'players' | 'bans' | 'schedules' | 'plugins' | 'audit' | 'settings'
type User = { username: string; role: string }

const nav: { page: Page; label: string; icon: typeof LayoutDashboard }[] = [
  { page: 'overview', label: 'Overview', icon: LayoutDashboard },
  { page: 'servers', label: 'Servers', icon: ServerIcon },
  { page: 'console', label: 'Live Console', icon: Terminal },
  { page: 'players', label: 'Players', icon: Users },
  { page: 'bans', label: 'Ban Manager', icon: BanIcon },
  { page: 'schedules', label: 'Scheduler', icon: CalendarClock },
  { page: 'plugins', label: 'Plugins', icon: Plug },
  { page: 'audit', label: 'Audit Log', icon: History },
  { page: 'settings', label: 'Settings', icon: Settings },
]

const fmtBytes = (bytes: number) => bytes ? `${(bytes / 1024 / 1024).toFixed(0)} MB` : '0 MB'
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
  const [page, setPage] = useState<Page>('overview')
  const [overview, setOverview] = useState<Overview | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
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
  const servers = overview?.servers ?? []

  const logout = async () => { await api('/auth/logout', { method: 'POST' }).catch(() => {}); onLogout() }
  return <div className="app-shell">
    <aside className={drawer ? 'open' : ''}>
      <div className="brand"><div className="brand-mark"><Shield size={24}/></div><div><strong>SCP CONTROL</strong><span>ADMINISTRATION</span></div></div>
      <nav>{nav.map(item => <button key={item.page} className={page === item.page ? 'active' : ''} onClick={() => { setPage(item.page); setDrawer(false) }}><item.icon size={18}/>{item.label}</button>)}</nav>
      <div className="aside-bottom"><div className="system-line"><span className="status-dot"/>System operational</div><div className="profile"><div className="avatar">{user.username.slice(0, 2).toUpperCase()}</div><div><strong>{user.username}</strong><span>{user.role}</span></div><button onClick={logout} title="Log out"><LogOut size={17}/></button></div></div>
    </aside>
    <main className="workspace">
      <header><button className="mobile-menu" onClick={() => setDrawer(!drawer)}>{drawer ? <X/> : <Menu/>}</button><div><span className="crumb">SCP CONTROL / </span>{nav.find(x => x.page === page)?.label.toUpperCase()}</div><div className="header-right"><span className="live-pill"><span className="status-dot"/> LIVE</span><button className="icon-button" onClick={load}><RefreshCw size={17}/></button></div></header>
      {error && <div className="toast error">{error}<button onClick={() => setError('')}><X size={15}/></button></div>}
      <div className="content">
        {page === 'overview' && <OverviewPage data={overview} setPage={setPage} setSelected={setSelected}/>}
        {page === 'servers' && <ServersPage servers={servers} refresh={load} setPage={setPage} setSelected={setSelected} onError={setError}/>}
        {page === 'console' && <ConsolePage servers={servers} selected={selected} setSelected={setSelected} onError={setError}/>}
        {page === 'players' && <EmptyPage icon={Users} title="Player intelligence" text="Live players appear when the SCP:SL query bridge is connected."/>}
        {page === 'bans' && <BansPage onError={setError}/>}
        {page === 'schedules' && <SchedulesPage servers={servers} onError={setError}/>}
        {page === 'plugins' && <PluginsPage servers={servers} selected={selected} setSelected={setSelected}/>}
        {page === 'audit' && <AuditPage/>}
        {page === 'settings' && <SettingsPage/>}
      </div>
    </main>
  </div>
}

function PageTitle({ eyebrow, title, children }: { eyebrow: string; title: string; children?: React.ReactNode }) {
  return <div className="page-title"><div><span className="eyebrow">{eyebrow}</span><h1>{title}</h1></div>{children && <div className="actions">{children}</div>}</div>
}

function OverviewPage({ data, setPage, setSelected }: { data: Overview | null; setPage: (p: Page) => void; setSelected: (id: string) => void }) {
  if (!data) return <Skeleton/>
  const cards = [
    { label: 'SERVERS ONLINE', value: `${data.serversOnline}/${data.serversTotal}`, sub: data.serversTotal ? 'Fleet availability' : 'Add your first server', icon: ServerIcon },
    { label: 'PLAYERS ONLINE', value: data.playersOnline, sub: 'Across all instances', icon: Users },
    { label: 'MEMORY IN USE', value: fmtBytes(data.memoryBytes), sub: 'Managed processes', icon: Activity },
    { label: 'FACILITY STATUS', value: data.serversOnline ? 'ACTIVE' : 'STANDBY', sub: 'Control plane healthy', icon: CircleGauge },
  ]
  return <>
    <PageTitle eyebrow="FACILITY COMMAND" title="Operations overview"><button className="primary" onClick={() => setPage('servers')}><Plus size={16}/> ADD SERVER</button></PageTitle>
    <section className="stat-grid">{cards.map(card => <article className="stat-card" key={card.label}><div className="card-top"><span>{card.label}</span><card.icon size={19}/></div><strong>{card.value}</strong><small>{card.sub}</small></article>)}</section>
    <section className="split-grid">
      <article className="panel">
        <div className="panel-head"><div><span className="eyebrow">INFRASTRUCTURE</span><h2>Server fleet</h2></div><button className="text-button" onClick={() => setPage('servers')}>VIEW ALL <ChevronRight size={15}/></button></div>
        {data.servers.length ? <div className="server-list">{data.servers.slice(0, 5).map(server =>
          <button className="server-row" key={server.id} onClick={() => { setSelected(server.id); setPage('console') }}>
            <span className={`server-state ${server.state}`}><Gamepad2 size={19}/></span><span className="server-name"><strong>{server.name}</strong><small>PID {server.processId ?? '—'} • {server.players}/{server.maxPlayers || '—'} players</small></span>
            <span className={`state-label ${server.state}`}><span/> {server.state}</span><span>{server.cpuPercent}% CPU</span><span>{fmtBytes(server.memoryBytes)}</span><ChevronRight size={16}/>
          </button>)}</div> : <EmptyMini text="No servers configured yet."/>}
      </article>
      <article className="panel">
        <div className="panel-head"><div><span className="eyebrow">SECURITY RECORD</span><h2>Recent activity</h2></div><button className="text-button" onClick={() => setPage('audit')}>AUDIT LOG <ChevronRight size={15}/></button></div>
        {data.recentActivity.length ? <div className="activity-list">{data.recentActivity.slice(0, 6).map(entry => <div className="activity-row" key={entry.id}><div className="event-icon"><Command size={16}/></div><div><strong>{entry.action}</strong><p>{entry.actor} · {entry.target}</p></div><time>{fmtAgo(entry.at)}</time></div>)}</div> : <EmptyMini text="Activity will appear here."/>}
      </article>
    </section>
  </>
}

function ServersPage({ servers, refresh, setPage, setSelected, onError }: { servers: Server[]; refresh: () => void; setPage: (p: Page) => void; setSelected: (id: string) => void; onError: (e: string) => void }) {
  const [modal, setModal] = useState(false)
  const action = async (id: string, name: string) => {
    try { await api(`/servers/${id}/${name}`, { method: 'POST' }); setTimeout(refresh, 600) }
    catch (e) { onError(e instanceof Error ? e.message : 'Action failed') }
  }
  return <>
    <PageTitle eyebrow="INFRASTRUCTURE" title="Server fleet"><button className="primary" onClick={() => setModal(true)}><Plus size={16}/> REGISTER SERVER</button></PageTitle>
    <div className="server-cards">{servers.map(server => <article className="server-card" key={server.id}>
      <div className="server-card-head"><div className={`server-state ${server.state}`}><Gamepad2/></div><div><h2>{server.name}</h2><span className={`state-label ${server.state}`}><span/> {server.state}</span></div><button className="icon-button" onClick={() => { setSelected(server.id); setPage('console') }}><Terminal size={18}/></button></div>
      <div className="metric-strip"><div><span>PROCESS</span><strong>{server.processId ?? '—'}</strong></div><div><span>CPU</span><strong>{server.cpuPercent}%</strong></div><div><span>MEMORY</span><strong>{fmtBytes(server.memoryBytes)}</strong></div><div><span>PLAYERS</span><strong>{server.players}/{server.maxPlayers || '—'}</strong></div></div>
      {server.lastError && <p className="error">{server.lastError}</p>}
      <div className="server-actions"><button disabled={server.state === 'online'} onClick={() => action(server.id, 'start')}><Play size={15}/> START</button><button disabled={server.state === 'offline'} onClick={() => action(server.id, 'restart')}><RotateCcw size={15}/> RESTART</button><button disabled={server.state === 'offline'} className="danger" onClick={() => action(server.id, 'stop')}><Square size={14}/> STOP</button></div>
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

function ConsolePage({ servers, selected, setSelected, onError }: { servers: Server[]; selected: string | null; setSelected: (id: string) => void; onError: (e: string) => void }) {
  const [lines, setLines] = useState<{ at: string; stream: string; line: string }[]>([])
  const [command, setCommand] = useState('')
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
      connection.start().then(() => connection?.invoke('JoinServer', selected)).catch(() => {})
    })
    return () => { disposed = true; connection?.stop() }
  }, [selected])
  return <>
    <PageTitle eyebrow="REAL-TIME OPERATIONS" title="Live console"><select value={selected ?? ''} onChange={e => { setSelected(e.target.value); setLines([]) }}><option value="">Select server</option>{servers.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select></PageTitle>
    <section className="console-panel"><div className="console-toolbar"><div><span className={`status-dot ${server?.state !== 'online' ? 'off' : ''}`}/>{server?.name ?? 'NO SERVER SELECTED'} <small>{server?.state.toUpperCase()}</small></div><button onClick={() => setLines([])}>CLEAR</button></div>
      <div className="console-output">{lines.length ? lines.map((line, i) => <div key={i} className={line.stream}><time>{new Date(line.at).toLocaleTimeString()}</time><span>{line.line}</span></div>) : <div className="console-placeholder"><Terminal size={28}/><span>Console output will stream here.</span></div>}</div>
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

function PluginsPage({ servers, selected, setSelected }: { servers: Server[]; selected: string | null; setSelected: (id: string) => void }) {
  const [plugins, setPlugins] = useState<{ name: string; version: string; framework: string; path: string }[]>([])
  useEffect(() => { if (selected) api<typeof plugins>(`/plugins/${selected}`).then(setPlugins).catch(() => setPlugins([])) }, [selected])
  return <><PageTitle eyebrow="EXTENSIONS" title="Plugin inventory"><select value={selected ?? ''} onChange={e => setSelected(e.target.value)}><option value="">Select server</option>{servers.map(x => <option value={x.id} key={x.id}>{x.name}</option>)}</select></PageTitle>
    <Table headers={['PLUGIN','FRAMEWORK','VERSION','LOCATION']}>{plugins.map(x => <tr key={x.path}><td><strong>{x.name}</strong></td><td><span className="tag">{x.framework}</span></td><td>{x.version}</td><td className="mono">{x.path}</td></tr>)}</Table>{!plugins.length && <EmptyPage icon={Plug} title="No plugins detected" text="EXILED and NWAPI plugin folders are scanned automatically."/>}
  </>
}

function AuditPage() {
  const [entries, setEntries] = useState<AuditEntry[]>([])
  useEffect(() => { api<AuditEntry[]>('/audit?take=250').then(setEntries) }, [])
  return <><PageTitle eyebrow="SECURITY RECORD" title="Audit log"/><Table headers={['TIME','ACTOR','ACTION','TARGET','DETAIL']}>{entries.map(x => <tr key={x.id}><td>{new Date(x.at).toLocaleString()}</td><td><strong>{x.actor}</strong></td><td><span className="tag">{x.action}</span></td><td>{x.target}</td><td>{x.detail}</td></tr>)}</Table>{!entries.length && <EmptyMini text="No activity recorded."/>}</>
}

function SettingsPage() {
  return <><PageTitle eyebrow="SYSTEM" title="Panel settings"/><section className="settings-grid"><article className="panel"><FileCode2 size={22}/><h2>Configuration</h2><p>Panel settings live in <code>appsettings.json</code>. Environment variables can override every production value.</p></article><article className="panel"><Shield size={22}/><h2>Security</h2><p>Change the bootstrap password before exposing the panel. Place it behind HTTPS and restrict network access.</p></article><article className="panel"><Activity size={22}/><h2>Remote agents</h2><p>The process adapter is ready to be separated into authenticated per-node agents in the next deployment phase.</p></article></section></>
}

function Table({ headers, children }: { headers: string[]; children: React.ReactNode }) {
  return <div className="table-wrap"><table><thead><tr>{headers.map(x => <th key={x}>{x}</th>)}</tr></thead><tbody>{children}</tbody></table></div>
}

function EmptyPage({ icon: Icon, title, text, children }: { icon: typeof Users; title: string; text: string; children?: React.ReactNode }) {
  return <section className="empty-page"><div className="empty-icon"><Icon size={28}/></div><h2>{title}</h2><p>{text}</p>{children}</section>
}
function EmptyMini({ text }: { text: string }) { return <div className="empty-mini">{text}</div> }
function Skeleton() { return <div className="skeleton"><div/><div/><div/><div/></div> }
