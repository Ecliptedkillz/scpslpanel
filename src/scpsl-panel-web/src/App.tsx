import { FormEvent, useCallback, useEffect, useRef, useState } from 'react'
import {
  Activity, ArrowLeft, Ban as BanIcon, Bot, CalendarClock, ChevronRight, CircleGauge, Command, Copy,
  Eye, FileCode2, FolderOpen, Gamepad2, History, LayoutDashboard, LogOut, Menu, Pencil, Play, Plug,
  Save, Plus, RefreshCw, RotateCcw, Search, Server as ServerIcon, Settings, Shield,
  Square, Sun, Moon, Terminal, Trash2, Users, X,
} from 'lucide-react'
import { api, ApiError } from './api'
import { QRCodeSVG } from 'qrcode.react'
import { ServerConfigEditor } from './components/ServerConfigEditor'
import { PlayerHistoryView, type StoredPlayer } from './components/PlayerHistoryView'
import { GlobalPlayerDatabase } from './components/GlobalPlayerDatabase'
import type { AuditEntry, Ban, BridgeSetup, BridgeStatus, Overview, Player, Schedule, Server } from './types'

type Page = 'overview' | 'servers' | 'server' | 'players' | 'reports' | 'incidents' | 'permissions' | 'donors' | 'bans' | 'schedules' | 'audit' | 'admins' | 'settings'
type ServerTab = 'overview' | 'operations' | 'monitoring' | 'console' | 'players' | 'player-history' | 'activity' | 'restarts' | 'plugins' | 'files' | 'maintenance'
type ServerAccessGrant = { serverId: string; permissions: string[] }
type User = { username: string; role: string; serverIds: string[]; permissions: string[]; serverAccess?: ServerAccessGrant[]; twoFactorEnabled?: boolean; discordLinked?: boolean; discordUsername?: string }
type ThemeMode = 'dark' | 'light' | 'system'
type SearchResult = { type: 'server' | 'player' | 'audit'; title: string; subtitle: string; serverId?: string; playerId?: string }
const hasServerPermission = (user: User, serverId: string, permission: string) => {
  if (user.role === 'Owner') return true
  const permissions = user.serverAccess?.find(grant => grant.serverId === serverId)?.permissions ?? user.permissions
  return permissions.includes(permission)
}

const nav: { page: Page; label: string; icon: typeof LayoutDashboard }[] = [
  { page: 'overview', label: 'Overview', icon: LayoutDashboard },
  { page: 'servers', label: 'Servers', icon: ServerIcon },
  { page: 'players', label: 'Player Database', icon: Users },
  { page: 'reports', label: 'Report Tickets', icon: Shield },
  { page: 'incidents', label: 'Incidents', icon: Activity },
  { page: 'permissions', label: 'In-game Permissions', icon: Shield },
  { page: 'donors', label: 'Donors & Badges', icon: Users },
  { page: 'bans', label: 'Ban Manager', icon: BanIcon },
  { page: 'schedules', label: 'Scheduler', icon: CalendarClock },
  { page: 'audit', label: 'Audit Log', icon: History },
  { page: 'admins', label: 'Admin Manager', icon: Shield },
  { page: 'settings', label: 'Settings', icon: Settings },
]

const fmtBytes = (bytes: number) => bytes ? `${(bytes / 1024 / 1024).toFixed(0)} MB` : '0 MB'
const fmtState = (state: unknown) => typeof state === 'string' ? state.toUpperCase() : 'UNKNOWN'
const topLevelPages = new Set<Page>(['overview', 'servers', 'players', 'reports', 'incidents', 'permissions', 'donors', 'bans', 'schedules', 'audit', 'admins', 'settings'])
const serverTabs = new Set<ServerTab>(['overview', 'operations', 'monitoring', 'console', 'players', 'player-history', 'activity', 'restarts', 'plugins', 'files', 'maintenance'])
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
function ServerGlyph({server,size=22}:{server:Pick<Server,'icon'|'accentColor'>;size?:number}){
  const props={size,style:{color:server.accentColor||'var(--red)'}}
  return server.icon==='shield'?<Shield {...props}/>:server.icon==='activity'?<Activity {...props}/>:server.icon==='server'?<ServerIcon {...props}/>:<Gamepad2 {...props}/>
}
function useConfirmDialog(){
  const [request,setRequest]=useState<{title:string;message:string;confirm:string;danger:boolean;requiredText?:string;resolve:(value:boolean)=>void}|null>(null)
  const [typed,setTyped]=useState('')
  const ask=(title:string,message:string,confirm='CONFIRM',danger=true,requiredText?:string)=>new Promise<boolean>(resolve=>{setTyped('');setRequest({title,message,confirm,danger,requiredText,resolve})})
  const close=(value:boolean)=>{request?.resolve(value);setRequest(null)}
  const valid=!request?.requiredText||typed===request.requiredText
  const dialog=request?<div className="modal-backdrop confirm-backdrop"><article className="modal confirm-dialog"><div className={`confirm-symbol ${request.danger?'danger':''}`}><Shield size={24}/></div><span className="eyebrow">CONFIRM ACTION</span><h2>{request.title}</h2><p>{request.message}</p>{request.requiredText&&<label className="typed-confirm">TYPE <strong>{request.requiredText}</strong> TO CONTINUE<input autoFocus autoComplete="off" value={typed} onChange={event=>setTyped(event.target.value)} /></label>}<footer><button onClick={()=>close(false)}>CANCEL</button><button disabled={!valid} className={request.danger?'danger solid':'primary'} onClick={()=>close(true)}>{request.confirm}</button></footer></article></div>:null
  return {ask,dialog}
}

function useUnsavedChanges(dirty:boolean){
  useEffect(()=>{const handler=(event:BeforeUnloadEvent)=>{if(!dirty)return;event.preventDefault();event.returnValue=''};window.addEventListener('beforeunload',handler);return()=>window.removeEventListener('beforeunload',handler)},[dirty])
}

export function App() {
  const [theme, setTheme] = useState<ThemeMode>(() => (localStorage.getItem('scpcontrol-theme') as ThemeMode) || 'dark')
  const [user, setUser] = useState<User | null | undefined>(undefined)
  useEffect(() => {
    document.documentElement.dataset.density=localStorage.getItem('scpcontrol-density')||'comfortable'
    const accent=localStorage.getItem('scpcontrol-accent')
    if(accent) document.documentElement.style.setProperty('--red',accent)
    document.documentElement.dataset.motion=localStorage.getItem('scpcontrol-motion')==='reduced'?'reduced':'full'
    document.documentElement.style.setProperty('--console-font-size',`${localStorage.getItem('scpcontrol-console-size')||14}px`)
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
  const [code, setCode] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [discordEnabled, setDiscordEnabled] = useState(false)
  useEffect(() => {
    api<{enabled:boolean}>('/auth/discord/status').then(value=>setDiscordEnabled(value.enabled)).catch(()=>{})
    const message = new URLSearchParams(location.search).get('discord_error')
    if (message) { setError(message); history.replaceState(null, '', location.pathname) }
  }, [])
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setBusy(true); setError('')
    try { onLogin(await api<User>('/auth/login', { method: 'POST', body: JSON.stringify({ username, password, code }) })) }
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
        <label>2FA CODE (IF ENABLED)<input inputMode="numeric" pattern="[0-9]{6}" maxLength={6} value={code} onChange={e=>setCode(e.target.value.replace(/\D/g,''))} autoComplete="one-time-code"/></label>
        {error && <p className="error">{error}</p>}
        <button className="primary wide" disabled={busy}>{busy ? 'AUTHENTICATING…' : 'AUTHENTICATE'} <ChevronRight size={17}/></button>
      </form>
      {discordEnabled&&<><div className="login-divider"><span>OR</span></div><a className="discord-login wide" href="/api/auth/discord/login"><Bot size={17}/> CONTINUE WITH DISCORD</a></>}
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
  const [success, setSuccess] = useState('')
  const [activityOpen, setActivityOpen] = useState(false)
  const [searchOpen, setSearchOpen] = useState(false)
  const [personalOpen, setPersonalOpen] = useState(false)
  const [operations, setOperations] = useState<Array<{id:string;type:string;target:string;status:string;message:string;createdAt:string}>>([])

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
    const refresh = () => api<typeof operations>('/operations?take=30').then(setOperations).catch(() => {})
    refresh(); const timer = setInterval(refresh, 3000)
    const notify = (event: Event) => { setSuccess((event as CustomEvent<string>).detail); setTimeout(() => setSuccess(''), 4000) }
    window.addEventListener('panel-success', notify)
    return () => { clearInterval(timer); window.removeEventListener('panel-success', notify) }
  }, [])
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
  useEffect(() => {
    const shortcut = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault(); setSearchOpen(value => !value)
      }
    }
    window.addEventListener('keydown', shortcut)
    return () => window.removeEventListener('keydown', shortcut)
  }, [])
  const servers = overview?.servers ?? []
  const visibleNav = user.role === 'Owner' ? nav : nav.filter(item =>
    !['permissions', 'bans', 'schedules', 'audit', 'admins'].includes(item.page)
    && (item.page !== 'donors' || user.serverAccess?.some(grant =>
      grant.permissions.includes('donors.manage') || grant.permissions.includes('badges.manage'))
      || user.permissions.includes('donors.manage') || user.permissions.includes('badges.manage'))
    && (item.page !== 'players' || user.serverAccess?.some(grant => grant.permissions.includes('players.history'))
      || user.permissions.includes('players.history'))
    && (item.page !== 'incidents' || user.serverAccess?.some(grant => grant.permissions.includes('monitoring'))
      || user.permissions.includes('monitoring')))
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
      <div className="aside-bottom"><div className="system-line"><span className="status-dot"/>System operational</div><div className="profile"><button className="profile-settings" onClick={()=>setPersonalOpen(true)} title="Personal settings"><div className="avatar">{user.username.slice(0, 2).toUpperCase()}</div><div><strong>{user.username}</strong><span>{user.role}</span></div><Settings size={15}/></button><button onClick={logout} title="Log out"><LogOut size={17}/></button></div></div>
    </aside>
    <main className="workspace">
      <header><button className="mobile-menu" onClick={() => setDrawer(!drawer)}>{drawer ? <X/> : <Menu/>}</button><div><span className="crumb">SCP CONTROL / </span>{page === 'server' ? selectedServer?.name.toUpperCase() ?? 'SERVER' : nav.find(x => x.page === page)?.label.toUpperCase()}</div><div className="header-right"><button className="global-search-trigger" onClick={()=>setSearchOpen(true)} title="Global search (Ctrl+K)"><Search size={15}/><span>SEARCH</span><kbd>CTRL K</kbd></button><span className="live-pill"><span className="status-dot"/> LIVE</span><button className="icon-button operation-trigger" title="Recent operations" onClick={() => setActivityOpen(!activityOpen)}><Activity size={17}/>{operations.some(x=>x.status==='queued'||x.status==='running')&&<span/>}</button><ThemeButton theme={theme} setTheme={setTheme}/><button className="icon-button" onClick={load}><RefreshCw size={17}/></button></div></header>
      {error && <div className="toast error">{error}<button onClick={() => setError('')}><X size={15}/></button></div>}
      {success && <div className="toast success">{success}<button onClick={() => setSuccess('')}><X size={15}/></button></div>}
      {activityOpen && <aside className="operation-drawer"><div className="operation-head"><div><span className="eyebrow">ACTIVITY</span><h2>Recent operations</h2></div><button className="icon-button" onClick={()=>setActivityOpen(false)}><X/></button></div>{operations.map(item=><div className={`operation-row ${item.status}`} key={item.id}><span className="operation-state"/><div><strong>{item.type} · {item.target}</strong><p>{item.message}</p><small>{new Date(item.createdAt).toLocaleString()}</small></div><b>{item.status}</b></div>)}{!operations.length&&<EmptyMini text="No recent operations."/>}</aside>}
      {searchOpen && <GlobalSearch close={()=>setSearchOpen(false)} openServer={openServer} openAudit={()=>navigatePage('audit')}/>}
      <div className="content">
        {page === 'overview' && <OverviewPage data={overview} navigatePage={navigatePage} openServer={openServer}/>}
        {page === 'servers' && <ServersPage user={user} servers={servers} refresh={load} openServer={openServer} onError={setError}/>}
        {page === 'players' && <GlobalPlayerDatabase onError={setError}/>}
        {page === 'reports' && <ReportTicketsPage user={user} servers={servers} onError={setError}/>}
        {page === 'incidents' && <IncidentManagementPage user={user} servers={servers} onError={setError}/>}
        {page === 'permissions' && user.role === 'Owner' && <IngamePermissionsPage servers={servers} onError={setError}/>}
        {page === 'donors' && (user.role === 'Owner' || servers.some(server =>
          hasServerPermission(user,server.id,'donors.manage') || hasServerPermission(user,server.id,'badges.manage')))
          && <DonorManagementPageV2 servers={servers} onError={setError}/>}
        {page === 'server' && <ServerWorkspace user={user} server={selectedServer} tab={serverTab} setTab={navigateServerTab} refresh={load} back={() => navigatePage('servers')} onError={setError}/>}
        {page === 'bans' && <BansPage onError={setError}/>}
        {page === 'schedules' && <SchedulesPage servers={servers} onError={setError}/>}
        {page === 'audit' && <AuditPage/>}
        {page === 'admins' && <AdminManagerPage user={user} servers={servers} onError={setError}/>}
        {page === 'settings' && <SettingsPage user={user} servers={servers} onError={setError}/>}
      </div>
    </main>
    {personalOpen&&<PersonalSettings user={user} onError={setError} close={()=>setPersonalOpen(false)}/>} 
  </div>
}

type ManagedIncident={id:string;serverId:string;title:string;category:string;severity:string;status:string;description:string;createdBy:string;createdAt:string;updatedAt:string;assignedTo?:string;resolution?:string;resolvedAt?:string;notes:Array<{id:string;text:string;actor:string;at:string}>}
function IncidentManagementPage({user,servers,onError}:{user:User;servers:Server[];onError:(message:string)=>void}){
  const [items,setItems]=useState<ManagedIncident[]>([])
  const [selected,setSelected]=useState<ManagedIncident|null>(null)
  const [creating,setCreating]=useState(false)
  const [filter,setFilter]=useState('active')
  const [note,setNote]=useState('')
  const [form,setForm]=useState({serverId:servers[0]?.id??'',title:'',category:'operations',severity:'medium',description:''})
  const load=useCallback(()=>api<ManagedIncident[]>('/incidents').then(values=>{setItems(values);setSelected(current=>current?values.find(item=>item.id===current.id)??null:null)}).catch(error=>onError(error.message)),[onError])
  useEffect(()=>{void load()},[load])
  useEffect(()=>{if(!form.serverId&&servers[0])setForm(value=>({...value,serverId:servers[0].id}))},[servers,form.serverId])
  const manageable=servers.filter(server=>hasServerPermission(user,server.id,'players.actions'))
  const visible=items.filter(item=>filter==='all'||(filter==='active'?!['resolved','dismissed'].includes(item.status):item.status===filter))
  const create=async(event:FormEvent)=>{event.preventDefault();try{await api('/incidents',{method:'POST',body:JSON.stringify(form)});setCreating(false);setForm({...form,title:'',description:''});await load()}catch(error){onError(error instanceof Error?error.message:'Unable to create incident')}}
  const update=async(changes:Partial<ManagedIncident>)=>{if(!selected)return;try{const updated=await api<ManagedIncident>(`/incidents/${selected.id}`,{method:'PUT',body:JSON.stringify({status:changes.status??selected.status,severity:changes.severity??selected.severity,assignedTo:changes.assignedTo??selected.assignedTo??'',resolution:changes.resolution??selected.resolution??''})});setSelected(updated);await load()}catch(error){onError(error instanceof Error?error.message:'Unable to update incident')}}
  const addNote=async(event:FormEvent)=>{event.preventDefault();if(!selected||!note.trim())return;try{const updated=await api<ManagedIncident>(`/incidents/${selected.id}/notes`,{method:'POST',body:JSON.stringify({text:note})});setSelected(updated);setNote('');await load()}catch(error){onError(error instanceof Error?error.message:'Unable to add incident note')}}
  return <section className="incident-center"><PageTitle eyebrow="OPERATOR WORKSPACE" title="Incident management">{manageable.length>0&&<button className="primary" onClick={()=>setCreating(true)}><Plus size={15}/> NEW INCIDENT</button>}</PageTitle><div className="incident-kpis"><div><strong>{items.filter(item=>item.status==='open').length}</strong><span>OPEN</span></div><div><strong>{items.filter(item=>item.status==='investigating').length}</strong><span>INVESTIGATING</span></div><div><strong>{items.filter(item=>item.severity==='critical'&&!['resolved','dismissed'].includes(item.status)).length}</strong><span>CRITICAL</span></div><div><strong>{items.filter(item=>item.status==='resolved').length}</strong><span>RESOLVED</span></div></div><nav className="incident-filters">{['active','open','investigating','resolved','all'].map(value=><button key={value} className={filter===value?'active':''} onClick={()=>setFilter(value)}>{value.toUpperCase()}</button>)}</nav><div className="incident-list">{visible.map(item=><button key={item.id} onClick={()=>setSelected(item)}><i className={item.severity}/><div><span className="eyebrow">{servers.find(server=>server.id===item.serverId)?.name??'SERVER'} · {item.category}</span><h3>{item.title}</h3><p>{item.description}</p></div><div><span className={`tag ${item.severity==='critical'||item.severity==='high'?'red':''}`}>{item.severity}</span><strong>{item.status}</strong><small>{fmtAgo(item.updatedAt)}</small></div></button>)}{!visible.length&&<EmptyMini text="No incidents match this view."/>}</div>
  {creating&&<div className="modal-backdrop"><form className="modal incident-create" onSubmit={create}><div className="modal-head"><div><span className="eyebrow">NEW INCIDENT</span><h2>Open an incident</h2></div><button type="button" className="icon-button" onClick={()=>setCreating(false)}><X/></button></div><label>SERVER<select required value={form.serverId} onChange={event=>setForm({...form,serverId:event.target.value})}>{manageable.map(server=><option key={server.id} value={server.id}>{server.name}</option>)}</select></label><label>TITLE<input required maxLength={120} value={form.title} onChange={event=>setForm({...form,title:event.target.value})}/></label><div className="form-row"><label>CATEGORY<select value={form.category} onChange={event=>setForm({...form,category:event.target.value})}><option value="operations">Operations</option><option value="moderation">Moderation</option><option value="security">Security</option><option value="outage">Outage</option><option value="maintenance">Maintenance</option></select></label><label>SEVERITY<select value={form.severity} onChange={event=>setForm({...form,severity:event.target.value})}><option value="low">Low</option><option value="medium">Medium</option><option value="high">High</option><option value="critical">Critical</option></select></label></div><label>DESCRIPTION<textarea required value={form.description} onChange={event=>setForm({...form,description:event.target.value})}/></label><div className="modal-actions"><button type="button" onClick={()=>setCreating(false)}>CANCEL</button><button className="primary">OPEN INCIDENT</button></div></form></div>}
  {selected&&<div className="modal-backdrop"><article className="modal incident-detail"><header><div><span className={`tag ${selected.severity==='critical'||selected.severity==='high'?'red':''}`}>{selected.severity.toUpperCase()}</span><h2>{selected.title}</h2><p>{servers.find(server=>server.id===selected.serverId)?.name} · Opened by {selected.createdBy} · {new Date(selected.createdAt).toLocaleString()}</p></div><button className="icon-button" onClick={()=>setSelected(null)}><X/></button></header><div className="incident-detail-grid"><main><span className="eyebrow">DESCRIPTION</span><p>{selected.description}</p><span className="eyebrow">TIMELINE</span><div className="incident-timeline"><div><i/><strong>Incident opened</strong><small>{selected.createdBy} · {new Date(selected.createdAt).toLocaleString()}</small></div>{selected.notes?.map(item=><div key={item.id}><i/><strong>{item.text}</strong><small>{item.actor} · {new Date(item.at).toLocaleString()}</small></div>)}</div><form className="incident-note" onSubmit={addNote}><input value={note} onChange={event=>setNote(event.target.value)} placeholder="Add an investigation note…"/><button className="primary">ADD NOTE</button></form></main><aside><label>STATUS<select value={selected.status} onChange={event=>void update({status:event.target.value})}><option value="open">Open</option><option value="investigating">Investigating</option><option value="resolved">Resolved</option><option value="dismissed">Dismissed</option></select></label><label>SEVERITY<select value={selected.severity} onChange={event=>void update({severity:event.target.value})}><option value="low">Low</option><option value="medium">Medium</option><option value="high">High</option><option value="critical">Critical</option></select></label><label>ASSIGNED TO<input value={selected.assignedTo??''} onChange={event=>setSelected({...selected,assignedTo:event.target.value})} onBlur={()=>void update({assignedTo:selected.assignedTo})} placeholder="Operator name"/></label><label>RESOLUTION<textarea value={selected.resolution??''} onChange={event=>setSelected({...selected,resolution:event.target.value})} placeholder="Outcome and follow-up actions"/></label><button onClick={()=>void update({resolution:selected.resolution})}>SAVE RESOLUTION</button></aside></div></article></div>}</section>
}

function GlobalSearch({close,openServer,openAudit}:{close:()=>void;openServer:(id:string,tab?:ServerTab)=>void;openAudit:()=>void}){
  const [query,setQuery]=useState('')
  const [results,setResults]=useState<SearchResult[]>([])
  const [loading,setLoading]=useState(false)
  useEffect(()=>{
    if(query.trim().length<2){setResults([]);setLoading(false);return}
    setLoading(true)
    const timer=setTimeout(()=>api<SearchResult[]>(`/search?q=${encodeURIComponent(query.trim())}`).then(setResults).catch(()=>setResults([])).finally(()=>setLoading(false)),220)
    return()=>clearTimeout(timer)
  },[query])
  useEffect(()=>{const escape=(event:KeyboardEvent)=>{if(event.key==='Escape')close()};window.addEventListener('keydown',escape);return()=>window.removeEventListener('keydown',escape)},[close])
  const choose=(result:SearchResult)=>{close();if(result.type==='audit')openAudit();else if(result.serverId)openServer(result.serverId,result.type==='player'?'player-history':'overview')}
  return <div className="modal-backdrop search-overlay" onMouseDown={event=>{if(event.target===event.currentTarget)close()}}><section className="global-search"><header><Search/><input autoFocus value={query} onChange={event=>setQuery(event.target.value)} placeholder="Search servers, players, Steam IDs, Discord IDs, audit events…"/><kbd>ESC</kbd></header><div className="global-search-results">{results.map((result,index)=><button key={`${result.type}:${result.serverId??''}:${result.playerId??index}:${index}`} onClick={()=>choose(result)}><span className={`search-result-icon ${result.type}`}>{result.type==='server'?<ServerIcon/>:result.type==='player'?<Users/>:<History/>}</span><span><strong>{result.title}</strong><small>{result.subtitle}</small></span><b>{result.type}</b><ChevronRight/></button>)}{loading&&<div className="empty-mini">Searching…</div>}{!loading&&query.trim().length<2&&<div className="search-hint"><Command/>Type at least two characters to search everything you can access.</div>}{!loading&&query.trim().length>=2&&!results.length&&<div className="empty-mini">No matching servers, players, or audit entries.</div>}</div><footer><span>CTRL K · OPEN SEARCH</span><span>ESC · CLOSE</span></footer></section></div>
}

type ReportTicket={id:string;serverId:string;createdAt:string;updatedAt?:string;status:string;reporterUserId:string;reporterName:string;targetUserId:string;targetName:string;reason:string;assignedTo?:string;resolution?:string}
function ReportTicketsPage({user,servers,onError}:{user:User;servers:Server[];onError:(value:string)=>void}){
  const [reports,setReports]=useState<ReportTicket[]|null>(null)
  const [filter,setFilter]=useState('open')
  const [selected,setSelected]=useState<ReportTicket|null>(null)
  const [resolution,setResolution]=useState('')
  const [busy,setBusy]=useState('')
  const [search,setSearch]=useState('')
  const load=useCallback(()=>api<ReportTicket[]>('/reports').then(setReports).catch(error=>onError(error.message)),[onError])
  useEffect(()=>{load();const timer=setInterval(load,5000);return()=>clearInterval(timer)},[load])
  const update=async(status:string)=>{
    if(!selected||busy)return
    if((status==='resolved'||status==='dismissed')&&!resolution.trim()){onError('Add an investigation outcome before closing this report.');return}
    setBusy(status)
    try{const updated=await api<ReportTicket>(`/reports/${selected.id}`,{method:'PUT',body:JSON.stringify({status,resolution:resolution.trim()||null})});setSelected(updated);setResolution(updated.resolution??'');setReports(current=>current?.map(item=>item.id===updated.id?updated:item)??null);window.dispatchEvent(new CustomEvent('panel-success',{detail:`Report ${status}.`}))}catch(error){onError(error instanceof Error?error.message:'Unable to update report')}finally{setBusy('')}
  }
  if(!reports)return <Skeleton/>
  const query=search.trim().toLowerCase()
  const visible=reports.filter(item=>(filter==='all'||item.status===filter)&&(!query||[item.reporterName,item.reporterUserId,item.targetName,item.targetUserId,item.reason,item.assignedTo].some(value=>value?.toLowerCase().includes(query))))
  const canManage=selected?hasServerPermission(user,selected.serverId,'players.actions'):false
  const serverName=(id:string)=>servers.find(server=>server.id===id)?.name??'Unknown server'
  return <><PageTitle eyebrow="MODERATION" title="In-game report tickets"><button onClick={load}><RefreshCw size={15}/> REFRESH</button></PageTitle>
    <section className="report-summary"><article><span>WAITING</span><strong>{reports.filter(x=>x.status==='open').length}</strong><small>Unclaimed reports</small></article><article><span>IN PROGRESS</span><strong>{reports.filter(x=>x.status==='claimed').length}</strong><small>Assigned to staff</small></article><article><span>RESOLVED</span><strong>{reports.filter(x=>x.status==='resolved').length}</strong><small>Completed investigations</small></article><article><span>DISMISSED</span><strong>{reports.filter(x=>x.status==='dismissed').length}</strong><small>Closed without action</small></article></section>
    <div className="report-toolbar"><nav>{['open','claimed','resolved','dismissed','all'].map(value=><button className={filter===value?'active':''} onClick={()=>setFilter(value)} key={value}>{value.toUpperCase()} <span>{value==='all'?reports.length:reports.filter(item=>item.status===value).length}</span></button>)}</nav><label><Search size={16}/><input value={search} onChange={event=>setSearch(event.target.value)} placeholder="Search player, ID, reason, staff…"/></label></div>
    <section className="panel report-list"><header><span>STATUS</span><span>REPORTER</span><span>TARGET</span><span>REASON</span><span>SERVER / AGE</span></header>{visible.map(item=><button className="report-row" key={item.id} onClick={()=>{setSelected(item);setResolution(item.resolution??'')}}><span className={`report-status ${item.status}`}><i/>{item.status.toUpperCase()}</span><strong>{item.reporterName}<small>{item.reporterUserId}</small></strong><strong>{item.targetName}<small>{item.targetUserId}</small></strong><p>{item.reason}</p><span>{serverName(item.serverId)}<small>{fmtAgo(item.createdAt)}{item.assignedTo?` · ${item.assignedTo}`:''}</small></span><ChevronRight size={15}/></button>)}{!visible.length&&<EmptyMini text="No report tickets match this view."/>}</section>
    {selected&&<div className="modal-backdrop"><article className="modal report-detail-modal"><header><div><span className={`report-status ${selected.status}`}><i/>{selected.status.toUpperCase()}</span><h2>Report against {selected.targetName}</h2><p>{serverName(selected.serverId)} · Ticket {selected.id.slice(0,8).toUpperCase()}</p></div><button className="icon-button" onClick={()=>setSelected(null)}><X/></button></header><div className="report-parties"><section><span>REPORTER</span><strong>{selected.reporterName}</strong><code>{selected.reporterUserId}</code></section><ChevronRight/><section><span>REPORTED PLAYER</span><strong>{selected.targetName}</strong><code>{selected.targetUserId}</code></section></div><section className="report-detail-body"><div><span className="eyebrow">REPORTED REASON</span><p className="report-reason">{selected.reason}</p></div><dl><dt>Created</dt><dd>{new Date(selected.createdAt).toLocaleString()}</dd><dt>Assigned to</dt><dd>{selected.assignedTo??'Unassigned'}</dd><dt>Last updated</dt><dd>{selected.updatedAt?new Date(selected.updatedAt).toLocaleString():'Not updated'}</dd></dl><label>INVESTIGATION OUTCOME <small>Required to resolve or dismiss</small><textarea disabled={!canManage} value={resolution} onChange={event=>setResolution(event.target.value)} placeholder="What did staff verify, and what action was taken?"/></label></section>{canManage?<footer className="report-actions"><div>{selected.status==='open'&&<button className="claim" disabled={!!busy} onClick={()=>void update('claimed')}><Shield size={15}/>{busy==='claimed'?'CLAIMING…':'CLAIM REPORT'}</button>}{selected.status==='claimed'&&<button disabled={!!busy} onClick={()=>void update('open')}><RotateCcw size={15}/> RELEASE</button>}{['resolved','dismissed'].includes(selected.status)&&<button disabled={!!busy} onClick={()=>void update('open')}><RotateCcw size={15}/> REOPEN</button>}</div><div>{!['resolved','dismissed'].includes(selected.status)&&<><button className="dismiss" disabled={!!busy||!resolution.trim()} onClick={()=>void update('dismissed')}><X size={15}/> DISMISS</button><button className="primary" disabled={!!busy||!resolution.trim()} onClick={()=>void update('resolved')}><Save size={15}/>{busy==='resolved'?'RESOLVING…':'RESOLVE REPORT'}</button></>}</div></footer>:<footer className="report-readonly"><Shield size={15}/> You have read-only access to this report.</footer>}</article></div>}
  </>
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
    <StaffDashboard/>
    <PermissionHealthDashboard/>
  </>
}

function PermissionHealthDashboard(){
  type Health={issues:Array<{severity:string}>;roles:Array<{groupName:string;serverName:string;onlinePlayers:number;bridgeConnected:boolean}>}
  const [health,setHealth]=useState<Health|null>(null)
  useEffect(()=>{api<Health>('/permissions/health').then(setHealth).catch(()=>{})},[])
  if(!health)return null
  const errors=health.issues.filter(issue=>issue.severity==='error').length
  const warnings=health.issues.filter(issue=>issue.severity==='warning').length
  const assigned=health.roles.reduce((count,role)=>count+role.onlinePlayers,0)
  const disconnected=new Set(health.roles.filter(role=>!role.bridgeConnected).map(role=>role.serverName))
  return <section className="panel permission-dashboard"><div className="panel-head"><div><span className="eyebrow">ACCESS HEALTH</span><h2>Runtime permissions</h2></div></div><div className="staff-summary"><div><strong>{health.roles.length}</strong><span>CONFIGURED ROLES</span></div><div><strong>{assigned}</strong><span>ONLINE ASSIGNMENTS</span></div><div><strong>{errors}</strong><span>CONFIG ERRORS</span></div><div><strong>{warnings}</strong><span>WARNINGS</span></div></div>{disconnected.size>0&&<p className="error">Permission bridge unavailable: {[...disconnected].join(', ')}</p>}</section>
}

function StaffDashboard(){
  type Data={watchlisted:number;failedOperations:number;bridgeIssues:Array<{id:string;name:string}>;recentModeration:Array<{currentName:string;type:string;reason:string;actor:string;at:string}>}
  const [data,setData]=useState<Data|null>(null)
  useEffect(()=>{api<Data>('/staff-dashboard').then(setData).catch(()=>{})},[])
  if(!data)return null
  return <section className="panel staff-dashboard"><div className="panel-head"><div><span className="eyebrow">STAFF WORKSPACE</span><h2>Attention required</h2></div></div><div className="staff-summary"><div><strong>{data.watchlisted}</strong><span>WATCHLISTED PLAYERS</span></div><div><strong>{data.bridgeIssues.length}</strong><span>BRIDGE ISSUES</span></div><div><strong>{data.failedOperations}</strong><span>FAILED OPERATIONS</span></div></div>{data.bridgeIssues.map(x=><p className="error" key={x.id}>Bridge disconnected: {x.name}</p>)}<div className="activity-list">{data.recentModeration.map((x,index)=><div className="activity-row" key={`${x.at}:${index}`}><span className="tag red">{x.type}</span><div><strong>{x.currentName}</strong><p>{x.reason} · {x.actor}</p></div><time>{fmtAgo(x.at)}</time></div>)}</div></section>
}

function ServersPage({ user, servers, refresh, openServer, onError }: { user: User; servers: Server[]; refresh: () => void; openServer: (id: string) => void; onError: (e: string) => void }) {
  const [modal, setModal] = useState(false)
  const [busyServer, setBusyServer] = useState<string | null>(null)
  const confirmation=useConfirmDialog()
  const action = async (id: string, name: string) => {
    if (busyServer) return
    const server = servers.find(item => item.id === id)
    if (name === 'restart' && !await confirmation.ask('Restart server?',`Restart ${server?.name ?? 'this server'}? Connected players may be disconnected.`,'RESTART SERVER')) return
    if (name === 'stop' && !await confirmation.ask('Stop server?',`Stop ${server?.name ?? 'this server'}? The panel will request a graceful shutdown.`,'STOP SERVER')) return
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
    {confirmation.dialog}
  </>
}

function AddServerModal({ close, saved, onError }: { close: () => void; saved: () => void; onError: (e: string) => void }) {
  const [form, setForm] = useState({ name: '', executablePath: '', arguments: '', workingDirectory: '', queryPort: 7777, autoRestart: true, autoStart: false, updateCommand: '', icon:'gamepad', accentColor:'#e44343' })
  const submit = async (e: FormEvent) => {
    e.preventDefault()
    try { await api('/servers', { method: 'POST', body: JSON.stringify(form) }); saved() }
    catch (err) { onError(err instanceof Error ? err.message : 'Unable to add server') }
  }
  return <div className="modal-backdrop" onMouseDown={e => e.target === e.currentTarget && close()}><form className="modal" onSubmit={submit}><div className="modal-head"><div><span className="eyebrow">NEW INFRASTRUCTURE</span><h2>Register server</h2></div><button type="button" className="icon-button" onClick={close}><X/></button></div>
    <label>DISPLAY NAME<input required placeholder="Site-02 Primary" value={form.name} onChange={e => setForm({...form, name: e.target.value})}/></label>
    <div className="form-row"><label>SERVER ICON<select value={form.icon} onChange={e=>setForm({...form,icon:e.target.value})}><option value="gamepad">Gamepad</option><option value="server">Server</option><option value="shield">Shield</option><option value="activity">Activity</option></select></label><label>ACCENT COLOR<input className="color-input" type="color" value={form.accentColor} onChange={e=>setForm({...form,accentColor:e.target.value})}/></label></div>
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
  const confirmation=useConfirmDialog()
  if (!server) return <EmptyPage icon={ServerIcon} title="Server not found" text="The selected server was removed or is no longer available."><button onClick={back}>BACK TO SERVERS</button></EmptyPage>
  const action = async (name: string) => {
    if (busy) return
    if (name === 'restart' && !await confirmation.ask('Restart server?',`Restart ${server.name}? Connected players may be disconnected.`,'RESTART SERVER')) return
    if (name === 'stop' && !await confirmation.ask('Stop server?',`Stop ${server.name}? The panel will request a graceful shutdown.`,'STOP SERVER')) return
    if (name === 'kill' && !await confirmation.ask('Force-kill server?',`Immediately terminate ${server.name} and its process tree. Unsaved game state may be lost.`,'FORCE KILL',true,server.name)) return
    setBusy(true)
    try { await api(`/servers/${server.id}/${name}`, { method: 'POST' }); await refresh() }
    catch (error) { onError(error instanceof Error ? error.message : 'Server action failed') }
    finally { setBusy(false) }
  }
  const removeServer=async()=>{
    if(server.state!=='offline'){onError('Stop the server before removing it.');return}
    if(!await confirmation.ask('Remove server?',`Permanently remove ${server.name} from the panel. Existing stored history is retained.`,'REMOVE SERVER',true,server.name))return
    try{await api(`/servers/${server.id}`,{method:'DELETE'});await refresh();back()}
    catch(error){onError(error instanceof Error?error.message:'Unable to remove server')}
  }
  const permissions = user.role === 'Owner' ? null : user.serverAccess?.find(x => x.serverId === server.id)?.permissions ?? user.permissions
  const allowed = (permission: string) => permissions === null || permissions.includes(permission)
  const tabs: { id: ServerTab; label: string; icon: typeof LayoutDashboard; permission: string }[] = [
    { id: 'overview', label: 'Overview', icon: LayoutDashboard, permission: 'view' },
    { id: 'operations', label: 'Round Control', icon: CircleGauge, permission: 'monitoring' },
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
  return <div className="server-workspace">
    <button className="back-button" onClick={back}><ArrowLeft size={15}/> ALL SERVERS</button>
    <section className="server-hero">
      <div className={`server-state ${server.state}`} style={{borderColor:server.accentColor}}><ServerGlyph server={server} size={28}/></div>
      <div><span className="eyebrow">MANAGED INSTANCE</span><h1>{server.name}</h1><span className={`state-label ${server.state}`}><span/> {fmtState(server.state)}</span></div>
      <div className="server-hero-actions">
        {allowed('server.start') && <button disabled={busy || server.state === 'online'} onClick={() => action('start')}><Play size={15}/> START</button>}
        {allowed('server.restart') && <button disabled={busy || server.state === 'offline'} onClick={() => action('restart')}><RotateCcw size={15}/> RESTART</button>}
        {allowed('server.stop') && <button disabled={busy || server.state === 'offline'} className="danger" onClick={() => action('stop')}><Square size={14}/> STOP</button>}
        {user.role==='Owner'&&<button disabled={busy||server.state==='offline'} className="danger" onClick={()=>action('kill')}><X size={14}/> FORCE KILL</button>}
        {user.role==='Owner'&&<button disabled={busy||server.state!=='offline'} className="danger" onClick={()=>void removeServer()}><Trash2 size={14}/> REMOVE</button>}
      </div>
    </section>
    <div className="server-tabs">{tabs.filter(item => allowed(item.permission)).map(item => <button key={item.id} className={tab === item.id ? 'active' : ''} onClick={() => setTab(item.id)}><item.icon size={16}/>{item.label}</button>)}</div>
    <div className="server-tab-content">
      {tab === 'overview' && <ServerOverview server={server} setTab={setTab}/>}
      {tab === 'operations' && <RoundControlCenter server={server} onError={onError} canAnnounce={allowed('announcements')} canRestart={allowed('server.restart')}/>}
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
    {confirmation.dialog}
  </div>
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
  const [view,setView]=useState<'performance'|'incidents'>('performance')
  const load = useCallback(() => Promise.all([
    api<Metric[]>(`/servers/${server.id}/metrics?hours=${hours}`),
    api<Incident[]>(`/servers/${server.id}/incidents`)
  ]).then(([samples, events]) => { setMetrics(samples); setIncidents(events) }).catch(e => onError(e.message)), [server.id, hours, onError])
  useEffect(() => { void load(); const timer = setInterval(load, 30000); return () => clearInterval(timer) }, [load])
  return <section><div className="section-toolbar"><div><span className="eyebrow">TELEMETRY</span><h2>Server monitoring</h2></div>{view==='performance'&&<select value={hours} onChange={e => setHours(Number(e.target.value))}><option value="1">Last hour</option><option value="6">Last 6 hours</option><option value="24">Last 24 hours</option><option value="168">Last 7 days</option></select>}</div><nav className="nested-tabs"><button className={view==='performance'?'active':''} onClick={()=>setView('performance')}>Performance</button><button className={view==='incidents'?'active':''} onClick={()=>setView('incidents')}>Incidents <span>{incidents.length}</span></button></nav>{view==='performance'&&<div className="chart-grid"><MetricChart title="CPU USAGE" values={metrics.map(x => x.cpuPercent)} suffix="%"/><MetricChart title="MEMORY" values={metrics.map(x => x.memoryBytes / 1024 / 1024)} suffix=" MB"/><MetricChart title="PLAYERS" values={metrics.map(x => x.players)} suffix=""/></div>}{view==='incidents'&&<article className="panel incident-panel"><div className="panel-head"><div><span className="eyebrow">DIAGNOSTICS</span><h2>Crash and restart history</h2></div></div>{incidents.length ? incidents.map(item => <div className="incident-row" key={item.id}><span className={`event-icon ${item.type.includes('failed') || item.type === 'crash' ? 'danger' : ''}`}><Activity size={15}/></span><div><strong>{item.type.replaceAll('-', ' ').toUpperCase()}</strong><p>{item.message}</p></div><time>{new Date(item.at).toLocaleString()}</time></div>) : <EmptyMini text="No incidents recorded."/ >}</article>}</section>
}

function MetricChart({ title, values, suffix }: { title: string; values: number[]; suffix: string }) {
  const width = 500, height = 130, max = Math.max(1, ...values)
  const points = values.map((value, index) => `${values.length === 1 ? width : index / (values.length - 1) * width},${height - value / max * (height - 15)}`).join(' ')
  const latest = values.at(-1) ?? 0
  return <article className="metric-chart"><div><span>{title}</span><strong>{latest.toFixed(title === 'PLAYERS' ? 0 : 1)}{suffix}</strong></div><svg viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none"><polyline points={points}/></svg><small>{values.length} samples · peak {max.toFixed(1)}{suffix}</small></article>
}

function RoundControlCenter({server,onError,canAnnounce,canRestart}:{server:Server;onError:(value:string)=>void;canAnnounce:boolean;canRestart:boolean}) {
  type Event = { id:string; at:string; type:string; displayName:string|null; detail:string }
  type Round = { id:string; startedAt:string; endedAt:string|null }
  const [status,setStatus]=useState<BridgeStatus|null>(null)
  const [events,setEvents]=useState<Event[]>([])
  const [rounds,setRounds]=useState<Round[]>([])
  const [announcement,setAnnouncement]=useState('')
  const [duration,setDuration]=useState(10)
  const [busy,setBusy]=useState('')
  const [,setClock]=useState(0)
  const confirmation=useConfirmDialog()
  const load=useCallback(async()=>{
    try {
      const [bridge,activity,history]=await Promise.all([
        api<BridgeStatus>(`/servers/${server.id}/players`),
        api<Event[]>(`/servers/${server.id}/activity?take=12`),
        api<Round[]>(`/servers/${server.id}/rounds?take=5`)
      ])
      setStatus(bridge);setEvents(activity);setRounds(history)
    } catch(error) { onError(error instanceof Error?error.message:'Unable to load round control') }
  },[server.id,onError])
  useEffect(()=>{void load();const poll=setInterval(load,3000);const clock=setInterval(()=>setClock(value=>value+1),1000);return()=>{clearInterval(poll);clearInterval(clock)}},[load])
  const activeRound=rounds.find(round=>!round.endedAt)
  const roundSeconds=activeRound?Math.max(0,Math.floor((Date.now()-new Date(activeRound.startedAt).getTime())/1000)):null
  const teamFor=(role:string)=>{
    const value=role.toLowerCase()
    if(value.includes('scp'))return'SCP'
    if(value.includes('classd')||value.includes('class-d')||value.includes('chaos'))return'CHAOS'
    if(value.includes('scientist')||value.includes('facility')||value.includes('ntf')||value.includes('guard'))return'FOUNDATION'
    if(value.includes('spectator')||value.includes('overwatch')||value.includes('tutorial'))return'SPECTATORS'
    return'OTHER'
  }
  const groups=['SCP','FOUNDATION','CHAOS','SPECTATORS','OTHER'].map(name=>({name,players:(status?.players??[]).filter(player=>teamFor(player.role)===name)})).filter(group=>group.players.length)
  const announce=async(event:FormEvent)=>{
    event.preventDefault();if(!announcement.trim()||busy)return
    setBusy('announce')
    try{await api(`/servers/${server.id}/announcement`,{method:'POST',body:JSON.stringify({message:announcement.trim(),durationSeconds:duration})});setAnnouncement('');window.dispatchEvent(new CustomEvent('panel-success',{detail:'Round announcement sent.'}))}
    catch(error){onError(error instanceof Error?error.message:'Unable to announce')}
    finally{setBusy('')}
  }
  const restart=async()=>{
    if(!await confirmation.ask('Restart server?',`Restart ${server.name}? Every connected player will be disconnected.`,'RESTART SERVER'))return
    setBusy('restart')
    try{await api(`/servers/${server.id}/restart`,{method:'POST'});window.dispatchEvent(new CustomEvent('panel-success',{detail:'Server restart requested.'}))}
    catch(error){onError(error instanceof Error?error.message:'Unable to restart server')}
    finally{setBusy('')}
  }
  const spectators=(status?.players??[]).filter(player=>teamFor(player.role)==='SPECTATORS').length
  return <section className="round-control">{confirmation.dialog}
    <header className="round-command-header panel"><div><span className="eyebrow">LIVE OPERATIONS</span><h2>Round Control Center</h2><p>Live bridge telemetry and permission-checked server controls.</p></div><div className={`round-phase ${status?.connected?'connected':''}`}><span>{status?.connected?'LIVE':'OFFLINE'}</span><strong>{(status?.roundState??'unknown').replaceAll('-',' ').toUpperCase()}</strong><small>{status?.lastSeenAt?`Heartbeat ${fmtAgo(status.lastSeenAt)}`:'Bridge unavailable'}</small></div></header>
    <div className="round-stat-strip"><article><small>ROUND TIME</small><strong>{roundSeconds==null?'—':formatPlaytime(roundSeconds)}</strong></article><article><small>PLAYERS</small><strong>{status?.players.length??0}<em> / {status?.maxPlayers||'—'}</em></strong></article><article><small>ALIVE / ACTIVE</small><strong>{(status?.players.length??0)-spectators}</strong></article><article><small>SPECTATORS</small><strong>{spectators}</strong></article></div>
    <div className="round-control-grid"><article className="panel round-roster"><div className="panel-head"><div><span className="eyebrow">LIVE ROSTER</span><h2>Players by faction</h2></div><span className="tag">{groups.length} GROUPS</span></div>
      {groups.map(group=><section className={`faction-block faction-${group.name.toLowerCase()}`} key={group.name}><header><strong>{group.name}</strong><span>{group.players.length}</span></header>{group.players.map(player=><div className="round-player" key={player.id}><i/><div><strong>{player.nickname}</strong><small>{player.role}</small></div><span>{player.ping||'—'} ms</span>{player.isMuted&&<b>MUTED</b>}</div>)}</section>)}
      {!groups.length&&<EmptyMini text={status?.connected?'No players are currently connected.':'Waiting for the LabAPI bridge.'}/>}</article>
      <aside className="round-side"><form className="panel round-announcement" onSubmit={announce}><div className="panel-head"><div><span className="eyebrow">BROADCAST</span><h2>Round announcement</h2></div></div><textarea disabled={!canAnnounce||!status?.connected} value={announcement} onChange={event=>setAnnouncement(event.target.value)} placeholder="Message every connected player…"/><div><select value={duration} onChange={event=>setDuration(Number(event.target.value))}><option value="5">5 seconds</option><option value="10">10 seconds</option><option value="15">15 seconds</option><option value="30">30 seconds</option></select><button className="primary" disabled={!canAnnounce||!status?.connected||busy==='announce'||!announcement.trim()}>SEND</button></div></form>
      <article className="panel round-actions"><div className="panel-head"><div><span className="eyebrow">SERVER ACTIONS</span><h2>Quick controls</h2></div></div><button className="danger" disabled={!canRestart||!!busy||server.state==='offline'} onClick={()=>void restart()}><RotateCcw/><span><strong>RESTART SERVER</strong><small>Confirmation required</small></span></button><p>Facility controls will appear here when supported by the installed bridge version.</p></article>
      <article className="panel round-events"><div className="panel-head"><div><span className="eyebrow">EVENT FEED</span><h2>Latest activity</h2></div></div>{events.slice(0,8).map(item=><div key={item.id}><span className="tag">{item.type.toUpperCase()}</span><p><strong>{item.displayName||'Server'}</strong>{item.detail?` · ${item.detail}`:''}</p><time>{fmtAgo(item.at)}</time></div>)}{!events.length&&<EmptyMini text="No recent bridge activity."/ >}</article></aside>
    </div>
  </section>
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
  const confirmation=useConfirmDialog()
  const load = useCallback(() => api<Backup[]>(`/servers/${server.id}/backups`).then(setBackups).catch(e => onError(e.message)), [server.id, onError])
  useEffect(() => { void load() }, [load])
  const run = async (action: 'backups' | 'update') => {
    if (action === 'update' && !await confirmation.ask('Run server update?',`Run the configured update command for ${server.name}? A backup will be created first.`,'BACK UP & UPDATE',true,server.name)) return
    setBusy(action)
    try { await api(`/servers/${server.id}/${action}`, {method:'POST'}); await load(); window.dispatchEvent(new CustomEvent('panel-success',{detail:action==='update'?'Server update completed.':'Backup created.'})) }
    catch(e) { onError(e instanceof Error ? e.message : `Unable to run ${action}`) }
    finally { setBusy('') }
  }
  const restore=async(item:Backup)=>{if(!await confirmation.ask('Restore this backup?',`This overwrites configuration files for ${server.name}. A safety backup is created first.`,'RESTORE BACKUP',true,server.name))return;setBusy(item.id);try{await api(`/servers/${server.id}/backups/${encodeURIComponent(item.fileName)}/restore`,{method:'POST'});await load();window.dispatchEvent(new CustomEvent('panel-success',{detail:`Restored ${item.fileName}.`}))}catch(e){onError(e instanceof Error?e.message:'Restore failed')}finally{setBusy('')}}
  return <section className="maintenance-layout">{confirmation.dialog}<article className="panel"><div className="panel-head"><div><span className="eyebrow">SAFE OPERATIONS</span><h2>Update and backup</h2></div></div><div className="maintenance-actions"><button onClick={() => run('backups')} disabled={!!busy}><Save/><div><strong>CREATE BACKUP</strong><span>Archive server configuration files</span></div></button><button onClick={() => run('update')} disabled={!!busy || server.state !== 'offline'}><RefreshCw/><div><strong>RUN UPDATE</strong><span>{server.state === 'offline' ? 'Backup, then execute update command' : 'Stop the server before updating'}</span></div></button></div></article><article className="panel"><div className="panel-head"><div><span className="eyebrow">RECOVERY</span><h2>Available backups</h2></div></div>{backups.map(item => <div className="backup-row" key={item.id}><FileCode2/><div><strong>{item.fileName}</strong><small>{new Date(item.createdAt).toLocaleString()} · {fmtBytes(item.sizeBytes)} · {item.actor}</small></div><div className="row-actions"><a className="manage-button" href={`/api/servers/${server.id}/backups/${encodeURIComponent(item.fileName)}`}>DOWNLOAD</a><button className="danger" disabled={!!busy||server.state!=='offline'} title={server.state!=='offline'?'Stop server before restoring':''} onClick={()=>restore(item)}>RESTORE</button></div></div>)}{!backups.length && <EmptyMini text="No backups created yet."/ >}</article></section>
}

function ServerPlayers({ server, onError, initialMode = 'live', moderation = {kick:true,mute:true,ban:true}, canAnnounce = true }: { server: Server; onError: (error: string) => void; initialMode?: 'live' | 'history'; moderation?: {kick:boolean;mute:boolean;ban:boolean}; canAnnounce?: boolean }) {
  const [status, setStatus] = useState<BridgeStatus | null>(null)
  const [setup, setSetup] = useState<BridgeSetup | null>(null)
  const [history, setHistory] = useState<StoredPlayer[]>([])
  const [profile, setProfile] = useState<StoredPlayer | null>(null)
  const [note, setNote] = useState('')
  const [announcement, setAnnouncement] = useState('')
  const [moderationDialog,setModerationDialog]=useState<{player:Player;action:'kick'|'ban'|'mute'|'unmute'}|null>(null)
  const [moderationReason,setModerationReason]=useState('')
  const [banMinutes,setBanMinutes]=useState(60)
  const [moderationBusy,setModerationBusy]=useState(false)
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
  const openModeration = (player:Player,action:'kick'|'ban'|'mute'|'unmute') => {
    setModerationDialog({player,action})
    setModerationReason(action==='kick'?'Removed by administrator':`${action} by moderator`)
    setBanMinutes(60)
  }
  const moderate = async () => {
    if(!moderationDialog||!moderationReason.trim())return
    const {player,action}=moderationDialog
    setModerationBusy(true)
    try {
      await api(`/servers/${server.id}/players/${player.id}/${action}`, {
        method: 'POST',
        body: JSON.stringify({ playerId: player.id, reason:moderationReason, durationMinutes:action==='ban'?banMinutes:null }),
      })
      setModerationDialog(null)
      void load()
    } catch (error) { onError(error instanceof Error ? error.message : `Unable to ${action} player`) }
    finally { setModerationBusy(false) }
  }
  if (initialMode === 'history') return <PlayerHistoryView server={server} history={history} profile={profile} setProfile={setProfile} note={note} setNote={setNote} reload={loadHistory} onError={onError}/>
  if (status?.connected) return <section className="players-panel">
    <div className="bridge-banner connected"><div><span className="status-dot"/><strong>LABAPI BRIDGE CONNECTED</strong><small>v{status.bridgeVersion} · LabAPI {status.apiVersion} · heartbeat {status.lastSeenAt ? fmtAgo(status.lastSeenAt) : 'now'}</small></div><span>{status.players.length}/{status.maxPlayers || '—'} PLAYERS</span></div>
    {canAnnounce && <form className="announcement-bar" onSubmit={async event => { event.preventDefault(); if (!announcement.trim()) return; try { await api(`/servers/${server.id}/announcement`, {method:'POST', body:JSON.stringify({message:announcement,durationSeconds:10})}); setAnnouncement('') } catch(error) { onError(error instanceof Error ? error.message : 'Unable to send announcement') } }}><input value={announcement} onChange={e => setAnnouncement(e.target.value)} placeholder="Broadcast an announcement to every player…"/><button className="primary">ANNOUNCE</button></form>}
    <Table headers={['PLAYER','USER ID','ROLE','PING / SESSION','VOICE','ACTIONS']}>{status.players.map(player => <tr key={player.id}><td><strong>{player.nickname}</strong><small>Player #{player.id} · {player.ipAddress || 'Identity protected'}</small></td><td className="mono">{player.userId || 'Do Not Track'}</td><td><span className="tag">{player.role}</span></td><td><strong>{player.ping || '—'} ms</strong><small>{formatPlaytime(player.sessionSeconds)}</small></td><td><span className={`tag ${player.isMuted ? 'red' : ''}`}>{player.isMuted ? 'MUTED' : 'OPEN'}</span></td><td><div className="row-actions">{moderation.mute && <button onClick={() => openModeration(player, player.isMuted ? 'unmute' : 'mute')}>{player.isMuted ? 'UNMUTE' : 'MUTE'}</button>}{moderation.kick && <button onClick={() => openModeration(player, 'kick')}>KICK</button>}{moderation.ban && <button className="danger" onClick={() => openModeration(player, 'ban')}>BAN</button>}</div></td></tr>)}</Table>
    {!status.players.length && <EmptyMini text="Bridge connected. No players are currently online."/>}
    {moderationDialog&&<div className="modal-backdrop"><form className="modal action-dialog moderation-dialog" onSubmit={e=>{e.preventDefault();void moderate()}}><header><div><span className="eyebrow">LIVE MODERATION</span><h2>{moderationDialog.action.toUpperCase()} PLAYER</h2><p>Apply this action to <strong>{moderationDialog.player.nickname}</strong>.</p></div><button type="button" className="icon-button" onClick={()=>setModerationDialog(null)}><X/></button></header><div className="action-dialog-body"><div className="moderation-target"><div className="avatar">{moderationDialog.player.nickname.slice(0,2).toUpperCase()}</div><div><strong>{moderationDialog.player.nickname}</strong><small>{moderationDialog.player.userId||`Player #${moderationDialog.player.id}`}</small></div></div><label>REASON<textarea autoFocus required value={moderationReason} onChange={e=>setModerationReason(e.target.value)}/></label>{moderationDialog.action==='ban'&&<label>BAN DURATION (MINUTES)<input type="number" min={1} max={525600} required value={banMinutes} onChange={e=>setBanMinutes(Number(e.target.value))}/></label>}<div className="action-warning"><Shield size={17}/><span>This command is sent through the LabAPI bridge and recorded in the audit and player history.</span></div></div><footer><button type="button" disabled={moderationBusy} onClick={()=>setModerationDialog(null)}>CANCEL</button><button className={moderationDialog.action==='ban'||moderationDialog.action==='kick'?'primary danger solid':'primary'} disabled={moderationBusy}>{moderationBusy?'WAITING FOR SERVER…':`CONFIRM ${moderationDialog.action.toUpperCase()}`}</button></footer></form></div>}
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
  type Event = { id: string; at: string; type: string; displayName: string | null; userId: string | null; detail: string; actor?: string | null; durationSeconds?: number | null }
  type Round = { id: string; startedAt: string; endedAt: string | null; leadingTeam: string | null; durationSeconds: number | null }
  const [events, setEvents] = useState<Event[]>([])
  const [rounds, setRounds] = useState<Round[]>([])
  const load = useCallback(async () => {
    try { const [activity, history] = await Promise.all([api<Event[]>(`/servers/${server.id}/activity`), api<Round[]>(`/servers/${server.id}/rounds`)]); setEvents(activity); setRounds(history) }
    catch(error) { onError(error instanceof Error ? error.message : 'Unable to load activity') }
  }, [server.id, onError])
  useEffect(() => { void load(); const timer = setInterval(load, 5000); return () => clearInterval(timer) }, [load])
  return <section className="activity-round-grid"><article className="panel"><div className="panel-head"><div><span className="eyebrow">PLAYER EVENTS</span><h2>Join, leave & moderation</h2></div></div><Table headers={['TIME','EVENT','PLAYER','ISSUED BY','DURATION / REASON']}>{events.map(item => <tr key={item.id}><td>{new Date(item.at).toLocaleString()}</td><td><span className={`tag ${['ban','oban','unban','kick','mute'].includes(item.type) ? 'red' : ''}`}>{item.type.toUpperCase()}</span></td><td><strong>{item.displayName || 'Server'}</strong><small>{item.userId || ''}</small></td><td>{item.actor || '—'}</td><td>{item.durationSeconds ? <small>{formatPlaytime(item.durationSeconds)}</small> : null}<strong>{item.detail || '—'}</strong></td></tr>)}</Table>{!events.length && <EmptyMini text="Bridge events will appear here."/>}</article><article className="panel"><div className="panel-head"><div><span className="eyebrow">ROUND TIMELINE</span><h2>Round history</h2></div></div><Table headers={['STARTED','ENDED','DURATION','RESULT']}>{rounds.map(round => <tr key={round.id}><td>{new Date(round.startedAt).toLocaleString()}</td><td>{round.endedAt ? new Date(round.endedAt).toLocaleString() : 'IN PROGRESS'}</td><td>{round.durationSeconds == null ? '—' : formatPlaytime(round.durationSeconds)}</td><td>{round.leadingTeam || '—'}</td></tr>)}</Table>{!rounds.length && <EmptyMini text="Completed rounds will appear here."/>}</article></section>
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
    <section className="split-grid schedule-grid"><form className="panel form-panel" onSubmit={async e => { e.preventDefault(); try { await api('/schedules', { method: 'POST', body: JSON.stringify(form) }); load(); window.dispatchEvent(new CustomEvent('panel-success',{detail:'Maintenance schedule created.'})) } catch (x) { onError(x instanceof Error ? x.message : 'Failed') } }}><div className="panel-head"><div><span className="eyebrow">NEW AUTOMATION</span><h2>Create schedule</h2></div></div><label>SERVER<select required value={form.serverId} onChange={e => setForm({...form, serverId: e.target.value})}><option value="">Choose instance</option>{servers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label><label>NAME<input value={form.name} onChange={e => setForm({...form, name:e.target.value})}/></label><div className="form-row"><label>CRON EXPRESSION<input value={form.cron} onChange={e => setForm({...form,cron:e.target.value})}/></label><label>ACTION<select value={form.action} onChange={e => setForm({...form,action:e.target.value})}><option value="restart">Warned restart</option><option value="start">Start</option><option value="stop">Stop</option><option value="backup">Create backup</option><option value="update">Backup and update</option><option value="backup-update-restart">Backup, update and start</option></select></label></div>{form.action === 'restart' && <label>PLAYER WARNING COUNTDOWN<select value={form.warningSeconds} onChange={e => setForm({...form,warningSeconds:Number(e.target.value)})}><option value="0">No warning</option><option value="60">1 minute</option><option value="300">5 minutes</option><option value="600">10 minutes</option><option value="1800">30 minutes</option></select></label>}<p className="muted">Updates require the server to already be offline. Backups are created automatically before every update.</p><button className="primary">CREATE SCHEDULE</button></form>
    <article className="panel"><div className="panel-head"><div><span className="eyebrow">AUTOMATION QUEUE</span><h2>Active schedules</h2></div></div>{items.length ? <div className="schedule-list">{items.map(x => <div className="schedule-row" key={x.id}><div className="event-icon"><CalendarClock size={17}/></div><div><strong>{x.name}</strong><p>{x.cron} · {x.action}</p></div><span className="tag">{x.enabled ? 'ENABLED' : 'PAUSED'}</span><button className="icon-button" onClick={async () => { await api(`/schedules/${x.id}`, { method: 'DELETE' }); load() }}><X size={16}/></button></div>)}</div> : <EmptyMini text="No scheduled actions."/ >}</article></section>
  </>
}

function PluginsPage({ servers, selected, setSelected, onError, embedded = false }: { servers: Server[]; selected: string | null; setSelected: (id: string) => void; onError: (e: string) => void; embedded?: boolean }) {
  type Plugin = { name: string; version: string; framework: string; enabled: boolean; path: string; configPaths: string[] }
  const [plugins, setPlugins] = useState<Plugin[]>([])
  const [busy, setBusy] = useState('')
  const [config, setConfig] = useState<{ plugin: string; path: string; content: string; originalContent: string } | null>(null)
  useUnsavedChanges(!!config&&config.content!==config.originalContent)
  const confirmation=useConfirmDialog()
  const load = useCallback(() => {
    if (selected) api<Plugin[]>(`/plugins/${selected}`).then(setPlugins).catch(e => onError(e.message))
  }, [selected])
  useEffect(() => { load() }, [load])
  const action = async (plugin: Plugin, name: 'load' | 'unload' | 'restart') => {
    if (!selected || !await confirmation.ask(`${name.toUpperCase()} plugin?`,`${name.toUpperCase()} ${plugin.name}? This performs a clean game-server restart.`,`${name.toUpperCase()} PLUGIN`)) return
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
      setConfig({ plugin: plugin.name, ...value, originalContent:value.content })
    } catch (e) { onError(e instanceof Error ? e.message : 'Unable to open plugin configuration') }
  }
  const saveConfig = async () => {
    if (!selected || !config) return
    setBusy(config.path)
    try {
      await api(`/plugins/${selected}/config`, { method: 'PUT', body: JSON.stringify({ path: config.path, content: config.content }) })
      setConfig({...config,originalContent:config.content})
      window.dispatchEvent(new CustomEvent('panel-success',{detail:'Plugin configuration saved.'}))
    } catch (e) { onError(e instanceof Error ? e.message : 'Unable to save plugin configuration') }
    finally { setBusy('') }
  }
  return <>{confirmation.dialog}{!embedded && <PageTitle eyebrow="EXTENSIONS" title="Plugin inventory"><select value={selected ?? ''} onChange={e => setSelected(e.target.value)}><option value="">Select server</option>{servers.map(x => <option value={x.id} key={x.id}>{x.name}</option>)}</select></PageTitle>}
    <Table headers={['PLUGIN','FRAMEWORK','VERSION','STATUS','ACTIONS']}>{plugins.map(x => <tr key={x.path}><td><strong>{x.name}</strong><small className="mono">{x.path}</small></td><td><span className="tag">{x.framework}</span></td><td>{x.version}</td><td><span className={`tag ${x.enabled ? '' : 'red'}`}>{x.enabled ? 'LOADED' : 'UNLOADED'}</span></td><td><div className="row-actions"><button disabled={busy === x.path} onClick={() => action(x, x.enabled ? 'unload' : 'load')}>{x.enabled ? 'UNLOAD' : 'LOAD'}</button><button disabled={busy === x.path || !x.enabled} onClick={() => action(x, 'restart')}><RefreshCw size={11}/> RESTART</button><button disabled={!x.configPaths?.length} onClick={() => openConfig(x)}><FileCode2 size={11}/> CONFIG {x.configPaths?.length ? `(${x.configPaths.length})` : ''}</button></div></td></tr>)}</Table>{!plugins.length && <EmptyPage icon={Plug} title="No plugins detected" text="LabAPI, EXILED and NWAPI plugin folders are scanned automatically."/>}
    {config && <section className="plugin-config"><div className="plugin-config-head"><div><span className="eyebrow">PLUGIN CONFIGURATION</span><h2>{config.plugin}</h2><label>CONFIG FILE<select value={config.path} onChange={e => { const plugin = plugins.find(x => x.name === config.plugin); if (plugin) void openConfig(plugin, e.target.value) }}>{plugins.find(x => x.name === config.plugin)?.configPaths.map(path => <option key={path} value={path}>{path.split(/[\\/]/).pop()}</option>)}</select></label><small className="mono">{config.path}</small></div><button className="icon-button" onClick={() => setConfig(null)}><X size={16}/></button></div><textarea className="code-editor" value={config.content} onChange={e => setConfig({ ...config, content: e.target.value })} spellCheck={false}/><div className="plugin-config-actions"><span>Save changes, then restart the plugin to apply them.</span><button className="primary" disabled={busy === config.path} onClick={saveConfig}><Save size={14}/> SAVE CONFIG</button></div></section>}
  </>
}

function AuditPage() {
  const [entries, setEntries] = useState<AuditEntry[]>([])
  const [filters,setFilters]=useState({query:'',actor:'',action:''})
  const params=()=>new URLSearchParams({take:'500',...filters}).toString()
  const load=()=>api<AuditEntry[]>(`/audit?${params()}`).then(setEntries)
  useEffect(() => { void load() }, [])
  return <><PageTitle eyebrow="SECURITY RECORD" title="Audit log"><a className="manage-button" href={`/api/audit/export?${params()}`}>EXPORT CSV</a></PageTitle><div className="panel audit-filters"><input placeholder="Search target or details" value={filters.query} onChange={e=>setFilters({...filters,query:e.target.value})}/><input placeholder="Actor" value={filters.actor} onChange={e=>setFilters({...filters,actor:e.target.value})}/><input placeholder="Action" value={filters.action} onChange={e=>setFilters({...filters,action:e.target.value})}/><button className="primary" onClick={load}>FILTER</button></div><Table headers={['TIME','ACTOR','ACTION','TARGET','DETAIL']}>{entries.map(x => <tr key={x.id}><td>{new Date(x.at).toLocaleString()}</td><td><strong>{x.actor}</strong></td><td><span className="tag">{x.action}</span></td><td>{x.target}</td><td>{x.detail}</td></tr>)}</Table>{!entries.length && <EmptyMini text="No matching activity."/>}</>
}

function AdminManagerPage({ user, servers, onError }: { user: User; servers: Server[]; onError: (e: string) => void }) {
  type Account = { id: string; username: string; role: string; enabled: boolean; serverIds: string[]; permissions: string[]; serverAccess?: ServerAccessGrant[] }
  const permissionGroups:AdminPermissionGroup[] = [
    {name:'Server',description:'Visibility and process controls',items:[['view','View server'],['server.start','Start server'],['server.stop','Stop server'],['server.restart','Restart server']]},
    {name:'Console',description:'Logs and remote commands',items:[['console.view','View console and download logs'],['console.write','Execute console commands']]},
    {name:'Players',description:'Live players, profiles, and moderation',items:[['players','View live players'],['players.history','View player database'],['players.notes','Add player notes'],['players.actions','Warnings, watchlist and allowlist'],['players.mute','Mute and unmute players'],['players.kick','Kick players'],['players.ban','Ban players']]},
    {name:'Configuration',description:'Plugins and configuration files',items:[['plugins','View plugins'],['plugins.manage','Load, unload, and restart plugins'],['config.view','Read configuration'],['config.write','Edit configuration']]},
    {name:'Operations',description:'Monitoring, announcements, and maintenance',items:[['monitoring','View monitoring and incidents'],['announcements','Send remote announcements'],['maintenance','Backups and server updates']]},
    {name:'Community',description:'Discord donor access and player cosmetics',items:[['donors.manage','Manage donor role mappings and synchronization'],['badges.manage','Manage custom role and user badges']]},
  ]
  const permissionOptions:ReadonlyArray<readonly [string,string]> = permissionGroups.flatMap(group=>group.items)
  const blank = { id: '', username: '', password: '', enabled: true, serverIds: [] as string[], permissions: [] as string[], serverAccess: [] as ServerAccessGrant[] }
  const [accounts, setAccounts] = useState<Account[]>([])
  const [form, setForm] = useState(blank)
  const [showModal, setShowModal] = useState(false)
  const [scopeServer, setScopeServer] = useState('')
  const [busy, setBusy] = useState(false)
  const [permissionSearch,setPermissionSearch]=useState('')
  const confirmation=useConfirmDialog()
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
  const setScopedPermissions=(permissions:string[])=>setForm({...form,serverAccess:form.serverAccess.map(grant=>grant.serverId===scopeServer?{...grant,permissions:[...new Set(permissions)]}:grant)})
  const togglePermissionGroup=(values:readonly string[])=>{
    const selected=form.serverAccess.find(x=>x.serverId===scopeServer)?.permissions??[]
    const allSelected=values.every(value=>selected.includes(value))
    setScopedPermissions(allSelected?selected.filter(value=>!values.includes(value)):([...selected,...values]))
  }
  const copyPermissionsToAssigned=()=>{
    const selected=form.serverAccess.find(x=>x.serverId===scopeServer)?.permissions??[]
    setForm({...form,serverAccess:form.serverAccess.map(grant=>({...grant,permissions:[...selected]}))})
  }
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
  const add = () => { setForm(blank); setScopeServer(''); setPermissionSearch(''); setShowModal(true) }
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
    if (!await confirmation.ask('Delete administrator?',`Delete ${account.username}? This cannot be undone.`,'DELETE ACCOUNT',true,account.username)) return
    try { await api(`/users/${account.id}`, { method: 'DELETE' }); loadAccounts() }
    catch (e) { onError(e instanceof Error ? e.message : 'Unable to delete account') }
  }
  const editorModal=showModal&&<AdminEditorModal form={form} setForm={setForm} servers={servers} scopeServer={scopeServer}
    setScopeServer={setScopeServer} permissionGroups={permissionGroups} permissionOptions={permissionOptions}
    permissionSearch={permissionSearch} setPermissionSearch={setPermissionSearch} toggleServer={toggleServer}
    togglePermission={togglePermission} togglePermissionGroup={togglePermissionGroup}
    setScopedPermissions={setScopedPermissions} copyPermissionsToAssigned={copyPermissionsToAssigned}
    applyPreset={applyPreset} busy={busy} close={()=>setShowModal(false)} save={save}/>
  return <>{confirmation.dialog}<PageTitle eyebrow="ACCESS CONTROL" title="Admin Manager"/>
    <section className="admin-manager-card"><div className="admin-manager-head"><div><h2>All Admins <span>({accounts.length})</span></h2><p>Manage panel accounts, server access, and operational permissions.</p></div><button className="primary" onClick={add}><Plus size={15}/> ADD ADMIN</button></div>
      <div className="admin-table-wrap"><table className="admin-table"><thead><tr><th>ADMIN</th><th>AUTH</th><th>SERVER ACCESS</th><th>PERMISSIONS</th><th>STATUS</th><th>ACTIONS</th></tr></thead><tbody>{accounts.map(account => { const grants = account.serverAccess?.length ? account.serverAccess : account.serverIds.map(serverId => ({serverId,permissions:account.permissions})); const permissionCount = new Set(grants.flatMap(x => x.permissions)).size; return <tr key={account.id}><td><div className="admin-identity"><div className="avatar">{account.username.slice(0,2).toUpperCase()}</div><div><strong>{account.username}</strong><small>{account.role}</small></div></div></td><td><span className="admin-auth" title="Password authentication">◆</span></td><td>{account.role === 'Owner' ? <span className="scope-all">ALL SERVERS</span> : <span>{grants.length} of {servers.length}</span>}</td><td>{account.role === 'Owner' ? <span className="scope-all">ALL PERMISSIONS</span> : <span>{permissionCount} permission type{permissionCount === 1 ? '' : 's'}</span>}</td><td><span className={`tag ${account.enabled ? '' : 'red'}`}>{account.enabled ? 'ENABLED' : 'DISABLED'}</span></td><td><div className="admin-actions">{account.role === 'Owner' ? <span className="your-account">YOUR ACCOUNT</span> : <><button className="edit" onClick={() => edit(account)}>EDIT</button><button className="delete" onClick={() => remove(account)}>DELETE</button></>}</div></td></tr>})}</tbody></table></div>
    </section>
    {showModal && <div className="modal-backdrop"><form className="modal admin-modal" onSubmit={save}><div className="modal-head"><div><span className="eyebrow">{form.id ? 'EDIT ADMINISTRATOR' : 'NEW ADMINISTRATOR'}</span><h2>{form.id ? form.username : 'Add admin'}</h2><p>Each assigned server has its own independent permission set.</p></div><button type="button" className="icon-button" onClick={() => setShowModal(false)}><X size={18}/></button></div><div className="admin-modal-body"><div className="form-row"><label>USERNAME<input required value={form.username} onChange={e => setForm({...form, username:e.target.value})}/></label><label>{form.id ? 'NEW PASSWORD (OPTIONAL)' : 'PASSWORD'}<input required={!form.id} minLength={8} type="password" value={form.password} onChange={e => setForm({...form,password:e.target.value})}/></label></div><label className="check-row"><input type="checkbox" checked={form.enabled} onChange={e => setForm({...form,enabled:e.target.checked})}/> Account enabled</label><div className="preset-row"><span>PRESET FOR SELECTED SERVER</span><button type="button" disabled={!scopeServer} onClick={() => applyPreset('viewer')}>VIEWER</button><button type="button" disabled={!scopeServer} onClick={() => applyPreset('moderator')}>MODERATOR</button><button type="button" disabled={!scopeServer} onClick={() => applyPreset('manager')}>MANAGER</button><button type="button" disabled={!scopeServer} onClick={() => applyPreset('full')}>FULL ACCESS</button></div><div className="admin-scope-grid"><section><span className="eyebrow">SERVER ACCESS</span>{servers.map(server => <div className={`server-grant-row ${scopeServer === server.id ? 'active' : ''}`} key={server.id}><label className="check-row"><input type="checkbox" checked={form.serverAccess.some(x => x.serverId === server.id)} onChange={() => toggleServer(server.id)}/><span><strong>{server.name}</strong><small>{server.state}</small></span></label>{form.serverAccess.some(x => x.serverId === server.id) && <button type="button" onClick={() => setScopeServer(server.id)}>EDIT PERMS</button>}</div>)}</section><section><span className="eyebrow">PERMISSIONS {scopeServer ? `· ${servers.find(x => x.id === scopeServer)?.name}` : ''}</span>{scopeServer ? permissionOptions.map(([value,label]) => <label className="check-row" key={value}><input type="checkbox" checked={form.serverAccess.find(x => x.serverId === scopeServer)?.permissions.includes(value) ?? false} onChange={() => togglePermission(value)}/>{label}</label>) : <EmptyMini text="Assign and select a server to configure its permissions."/>}</section></div></div><div className="modal-actions"><button type="button" onClick={() => setShowModal(false)}>CANCEL</button><button className="primary" disabled={busy}><Save size={14}/> {busy ? 'SAVING…' : 'SAVE ADMIN'}</button></div></form></div>}
    {editorModal}
  </>
}

type AdminEditorForm={id:string;username:string;password:string;enabled:boolean;serverIds:string[];permissions:string[];serverAccess:ServerAccessGrant[]}
type AdminPermissionGroup={name:string;description:string;items:ReadonlyArray<readonly [string,string]>}
function AdminEditorModal({form,setForm,servers,scopeServer,setScopeServer,permissionGroups,permissionOptions,
  permissionSearch,setPermissionSearch,toggleServer,togglePermission,togglePermissionGroup,setScopedPermissions,
  copyPermissionsToAssigned,applyPreset,busy,close,save}:{
  form:AdminEditorForm;setForm:React.Dispatch<React.SetStateAction<AdminEditorForm>>;servers:Server[];
  scopeServer:string;setScopeServer:(id:string)=>void;permissionGroups:ReadonlyArray<AdminPermissionGroup>;
  permissionOptions:ReadonlyArray<readonly [string,string]>;permissionSearch:string;setPermissionSearch:(value:string)=>void;
  toggleServer:(id:string)=>void;togglePermission:(value:string)=>void;togglePermissionGroup:(values:readonly string[])=>void;
  setScopedPermissions:(values:string[])=>void;copyPermissionsToAssigned:()=>void;
  applyPreset:(name:'viewer'|'moderator'|'manager'|'full')=>void;busy:boolean;close:()=>void;save:(event:FormEvent)=>Promise<void>
}){
  const selected=form.serverAccess.find(x=>x.serverId===scopeServer)?.permissions??[]
  return <div className="account-editor-overlay"><form className="account-editor" onSubmit={save}>
    <div className="account-editor-head"><div><span className="eyebrow">{form.id?'EDIT ADMINISTRATOR':'NEW ADMINISTRATOR'}</span><h2>{form.id?form.username:'Add administrator'}</h2><p>Assign servers and configure an independent permission set for each one.</p></div><button type="button" className="icon-button" onClick={close}><X size={18}/></button></div>
    <div className="account-editor-body">
      <section className="admin-account-section"><div className="admin-section-title"><span>01</span><div><h3>Account details</h3><p>Login credentials and account availability.</p></div></div><div className="form-row"><label>USERNAME<input required value={form.username} onChange={e=>setForm({...form,username:e.target.value})}/></label><label>{form.id?'NEW PASSWORD (OPTIONAL)':'PASSWORD'}<input required={!form.id} minLength={8} type="password" value={form.password} onChange={e=>setForm({...form,password:e.target.value})}/></label></div><label className="account-enabled-card"><input type="checkbox" checked={form.enabled} onChange={e=>setForm({...form,enabled:e.target.checked})}/><span><strong>Account enabled</strong><small>Allow this administrator to sign in.</small></span></label></section>
      <section className="account-editor-access"><div className="admin-section-title"><span>02</span><div><h3>Server access and permissions</h3><p>Select a server, then choose exactly what this administrator can do.</p></div></div><div className="account-editor-workspace">
        <aside className="account-editor-servers"><div className="selector-head"><span className="eyebrow">ASSIGNED SERVERS</span><b>{form.serverAccess.length}/{servers.length}</b></div>{servers.map(server=>{const assigned=form.serverAccess.some(x=>x.serverId===server.id);return <button type="button" className={`server-grant-row ${scopeServer===server.id?'active':''} ${assigned?'assigned':''}`} key={server.id} onClick={()=>assigned&&setScopeServer(server.id)}><input type="checkbox" checked={assigned} onClick={e=>e.stopPropagation()} onChange={()=>toggleServer(server.id)}/><span><strong>{server.name}</strong><small><i className={`server-dot ${server.state}`}/>{server.state}</small></span><b>{assigned?(scopeServer===server.id?'EDITING':'EDIT'):'NO ACCESS'}</b></button>})}</aside>
        <section className="account-editor-permissions">{scopeServer?<><div className="permission-selector-head"><div><span className="eyebrow">PERMISSIONS FOR</span><h3>{servers.find(x=>x.id===scopeServer)?.name}</h3><p>{selected.length} of {permissionOptions.length} selected</p></div><div className="permission-head-actions"><button type="button" onClick={()=>setScopedPermissions(permissionOptions.map(([value])=>value))}>SELECT ALL</button><button type="button" onClick={()=>setScopedPermissions([])}>CLEAR</button></div></div>
          <div className="permission-presets"><span>QUICK PRESET</span><button type="button" onClick={()=>applyPreset('viewer')}>VIEWER</button><button type="button" onClick={()=>applyPreset('moderator')}>MODERATOR</button><button type="button" onClick={()=>applyPreset('manager')}>MANAGER</button><button type="button" onClick={()=>applyPreset('full')}>FULL ACCESS</button></div>
          <div className="permission-tools"><label><Search size={15}/><input value={permissionSearch} onChange={e=>setPermissionSearch(e.target.value)} placeholder="Search permissions"/></label>{form.serverAccess.length>1&&<button type="button" onClick={copyPermissionsToAssigned}>COPY TO ALL ASSIGNED</button>}</div>
          <div className="permission-groups">{permissionGroups.map(group=>{const visible=group.items.filter(([value,label])=>`${value} ${label}`.toLowerCase().includes(permissionSearch.toLowerCase()));if(!visible.length)return null;const allSelected=group.items.every(([value])=>selected.includes(value));return <section className="permission-group" key={group.name}><header><div><h4>{group.name}</h4><p>{group.description}</p></div><button type="button" className={allSelected?'selected':''} onClick={()=>togglePermissionGroup(group.items.map(([value])=>value))}>{allSelected?'CLEAR GROUP':'SELECT GROUP'}</button></header><div>{visible.map(([value,label])=><label className={`permission-option ${selected.includes(value)?'selected':''}`} key={value}><input type="checkbox" checked={selected.includes(value)} onChange={()=>togglePermission(value)}/><span><strong>{label}</strong><code>{value}</code></span>{['server.stop','console.write','players.kick','players.ban','plugins.manage','config.write','maintenance'].includes(value)&&<em>SENSITIVE</em>}</label>)}</div></section>})}</div>
        </>:<div className="permission-empty"><Shield size={34}/><h3>Select an assigned server</h3><p>Enable a server on the left to configure its permissions.</p></div>}</section>
      </div></section>
    </div>
    <div className="modal-actions admin-modal-actions"><div><strong>{form.serverAccess.length} server{form.serverAccess.length===1?'':'s'} assigned</strong><span>Changes take effect after saving.</span></div><button type="button" onClick={close}>CANCEL</button><button className="primary" disabled={busy||!form.serverAccess.length}><Save size={14}/>{busy?'SAVING…':'SAVE ADMINISTRATOR'}</button></div>
  </form></div>
}

type IntegrationSettings = {
  discordWebhookUrl: string; notifyCrash: boolean; notifyRestart: boolean; notifyBridgeOffline: boolean
  notifyAdminActions: boolean; notifyHighCpu: boolean; highCpuPercent: number
  notifyHighMemory: boolean; highMemoryMb: number; alertCooldownMinutes: number
  crashMessage: string; bridgeOfflineMessage: string; highCpuMessage: string
  highMemoryMessage: string; restartFailureMessage: string; scheduleFailureMessage: string
  discordBotEnabled: boolean; discordBotToken: string; discordGuildId: string; discordControlRoleIds: string
  discordNotificationChannelId: string; steamWebApiKey: string
  discordModerationChannelId: string; discordReportChannelId: string; discordAuditChannelId: string
  discordRoleGrants: {roleId:string;serverId:string;permissions:string[]}[]
  discordGameRoleGrants: {roleId:string;serverId:string;groupName:string;priority:number;enabled:boolean;
    permissions:string[];inheritedGroups:string[];badgeText:string;badgeColor:string;hidden:boolean;
    cover:boolean;reservedSlot:boolean;kickPower:number;requiredKickPower:number;pluginPermissions:string[]}[]
  discordDonorRoleGrants: {roleId:string;serverId:string;tier:number;priority:number;enabled:boolean}[]
  customUserBadges: {serverId:string;steamId:string;badgeText:string;badgeColor:string}[]
  customRoleBadges: {roleId:string;serverId:string;badgeText:string;badgeColor:string;priority:number;enabled:boolean}[]
  discordDailyReportEnabled: boolean; discordDailyReportHourUtc: number
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
  discordBotEnabled: false, discordBotToken: '', discordGuildId: '', discordControlRoleIds: '',
  discordNotificationChannelId: '', steamWebApiKey: '',
  discordModerationChannelId: '', discordReportChannelId: '', discordAuditChannelId: '', discordRoleGrants: [],
  discordGameRoleGrants: [], discordDonorRoleGrants: [], customUserBadges: [], customRoleBadges: [],
  discordDailyReportEnabled: false, discordDailyReportHourUtc: 12,
}

function SettingsPage({ user, servers, onError }: { user: User; servers: Server[]; onError: (e: string) => void }) {
  const [tab,setTab]=useState<'general'|'appearance'|'discord'|'diagnostics'|'updates'|'alerts'|'delivery'>('general')
  const tabs: Array<[typeof tab,string]>=[['general','General']]
  tabs.splice(1,0,['appearance','Appearance'])
  if(user.role==='Owner') tabs.push(['discord','Discord'],['diagnostics','Diagnostics'],['updates','Update center'],['alerts','Alert rules'],['delivery','Delivery log'])
  return <><PageTitle eyebrow="SYSTEM" title="Settings"/><nav className="page-tabs settings-tabs">{tabs.map(([value,label])=><button key={value} className={tab===value?'active':''} onClick={()=>setTab(value)}>{label}</button>)}</nav><section className="settings-tab-content">
    {tab==='general'&&<article className="panel settings-section"><FileCode2 size={26}/><h2>Panel configuration</h2><p>Server access and permissions are managed in Admin Manager. Runtime settings are stored in <code>appsettings.json</code>.</p><div className="setting-info-row"><strong>Signed-in account</strong><span>{user.username}</span></div><div className="setting-info-row"><strong>Account role</strong><span>{user.role}</span></div><div className="setting-info-row"><strong>Registered servers</strong><span>{servers.length}</span></div></article>}
    {tab==='appearance'&&<AppearancePanel/>}
    {tab==='discord'&&user.role==='Owner'&&<DiscordBotPanel servers={servers} onError={onError}/>}
    {tab==='diagnostics'&&user.role==='Owner'&&<DiscordDiagnosticsPanel onError={onError}/>}
    {tab==='updates'&&user.role==='Owner'&&<UpdateCenterPanel onError={onError}/>}
    {tab==='alerts'&&user.role==='Owner'&&<AlertRulesPanel onError={onError}/>}
    {tab==='delivery'&&user.role==='Owner'&&<NotificationHistoryPanel onError={onError}/>}
  </section></>
}

function AppearancePanel(){
  const [density,setDensity]=useState(()=>localStorage.getItem('scpcontrol-density')||'comfortable')
  const [accent,setAccent]=useState(()=>localStorage.getItem('scpcontrol-accent')||'#e44343')
  const [reducedMotion,setReducedMotion]=useState(()=>localStorage.getItem('scpcontrol-motion')==='reduced')
  const [consoleSize,setConsoleSize]=useState(()=>Number(localStorage.getItem('scpcontrol-console-size')||14))
  const applyDensity=(value:string)=>{setDensity(value);localStorage.setItem('scpcontrol-density',value);document.documentElement.dataset.density=value}
  const applyAccent=(value:string)=>{setAccent(value);localStorage.setItem('scpcontrol-accent',value);document.documentElement.style.setProperty('--red',value)}
  const accents=['#e44343','#f97316','#eab308','#22c55e','#06b6d4','#6366f1','#a855f7','#ec4899']
  const applyMotion=(value:boolean)=>{setReducedMotion(value);localStorage.setItem('scpcontrol-motion',value?'reduced':'full');document.documentElement.dataset.motion=value?'reduced':'full'}
  const applyConsoleSize=(value:number)=>{setConsoleSize(value);localStorage.setItem('scpcontrol-console-size',String(value));document.documentElement.style.setProperty('--console-font-size',`${value}px`)}
  return <article className="panel settings-section appearance-panel"><Activity size={26}/><h2>Interface appearance</h2><p>Choose how much information fits on screen and personalize the panel accent.</p><section><span className="eyebrow">DISPLAY DENSITY</span><div className="choice-cards"><button className={density==='comfortable'?'active':''} onClick={()=>applyDensity('comfortable')}><strong>Comfortable</strong><small>Larger controls and generous spacing</small></button><button className={density==='compact'?'active':''} onClick={()=>applyDensity('compact')}><strong>Compact</strong><small>More rows and information on screen</small></button></div></section><section><span className="eyebrow">ACCENT COLOR</span><div className="accent-picker">{accents.map(color=><button key={color} className={accent===color?'active':''} style={{backgroundColor:color}} aria-label={`Use ${color}`} onClick={()=>applyAccent(color)}/>) }<label title="Custom accent"><input type="color" value={accent} onChange={e=>applyAccent(e.target.value)}/></label></div></section><section><span className="eyebrow">ACCESSIBILITY & CONSOLE</span><label className="check-row"><input type="checkbox" checked={reducedMotion} onChange={e=>applyMotion(e.target.checked)}/> Reduce interface motion</label><label>CONSOLE FONT SIZE ({consoleSize}px)<input type="range" min="12" max="20" value={consoleSize} onChange={e=>applyConsoleSize(Number(e.target.value))}/></label></section></article>
}

function PersonalSettings({user,onError,close}:{user:User;onError:(message:string)=>void;close:()=>void}) {
  const [tab,setTab]=useState<'password'|'security'>('password')
  return <div className="personal-settings-backdrop" onMouseDown={event=>{if(event.target===event.currentTarget)close()}}><section className="personal-settings-modal">
    <header><div><span className="eyebrow">PERSONAL SETTINGS</span><h2>{user.username}</h2><p>Manage your personal account and sign-in security.</p></div><button className="icon-button" onClick={close}><X size={18}/></button></header>
    <nav className="personal-settings-tabs"><button className={tab==='password'?'active':''} onClick={()=>setTab('password')}>PASSWORD</button><button className={tab==='security'?'active':''} onClick={()=>setTab('security')}>SECURITY &amp; SESSIONS</button></nav>
    <div className="personal-settings-body">{tab==='password'?<SettingsBasePage user={user} onError={onError}/>:<TwoFactorPanel user={user} onError={onError}/>}</div>
  </section></div>
}

function TwoFactorPanel({user,onError}:{user:User;onError:(message:string)=>void}) {
  const [setup,setSetup]=useState<{secret:string;uri:string}|null>(null)
  const [code,setCode]=useState('')
  const [sessions,setSessions]=useState<Array<{id:string;createdAt:string;lastSeenAt:string;ipAddress:string;userAgent:string;current:boolean}>>([])
  const confirmation=useConfirmDialog()
  const loadSessions=()=>api<typeof sessions>('/auth/sessions').then(setSessions).catch(e=>onError(e.message))
  useEffect(()=>{void loadSessions()},[])
  return <article className="panel settings-password">{confirmation.dialog}<Shield size={26}/><h2>Two-factor authentication</h2><p>{user.twoFactorEnabled?'Two-factor authentication is enabled.':'Protect your account with a TOTP authenticator app.'}</p>
    {setup&&<div className="totp-setup"><div className="totp-qr"><QRCodeSVG value={setup.uri} size={210} level="M" marginSize={2}/><small>SCAN WITH GOOGLE AUTHENTICATOR</small></div><div><p>Open Google Authenticator, tap <strong>+</strong>, choose <strong>Scan a QR code</strong>, then enter the generated six-digit code below.</p><label>MANUAL SETUP SECRET<div className="input-action"><input readOnly value={setup.secret}/><button type="button" onClick={()=>void copyText(setup.secret)}>COPY</button></div></label><details><summary>Show setup URI</summary><small className="mono totp-uri">{setup.uri}</small></details></div></div>}
    <label>6-DIGIT CODE<input inputMode="numeric" maxLength={6} value={code} onChange={e=>setCode(e.target.value.replace(/\D/g,''))}/></label>
    <div className="button-row">{!user.twoFactorEnabled&&!setup&&<button onClick={async()=>{try{setSetup(await api('/auth/2fa/setup',{method:'POST'}))}catch(e){onError(e instanceof Error?e.message:'Unable to begin setup')}}}>BEGIN SETUP</button>}{setup&&<button className="primary" onClick={async()=>{try{await api('/auth/2fa/confirm',{method:'POST',body:JSON.stringify({code})});location.reload()}catch(e){onError(e instanceof Error?e.message:'Invalid code')}}}>ENABLE 2FA</button>}{user.twoFactorEnabled&&<button className="danger" onClick={async()=>{try{await api('/auth/2fa/disable',{method:'POST',body:JSON.stringify({code})});location.reload()}catch(e){onError(e instanceof Error?e.message:'Invalid code')}}}>DISABLE 2FA</button>}<button className="danger" onClick={async()=>{if(!await confirmation.ask('Revoke all sessions?','Every device, including this one, will be signed out.','REVOKE ALL',true,'REVOKE ALL'))return;await api('/auth/sessions/revoke',{method:'POST'});location.reload()}}>REVOKE ALL SESSIONS</button></div>
    <div className="discord-connect"><span className="eyebrow">DISCORD LOGIN</span><div><Bot size={22}/><span><strong>{user.discordLinked ? user.discordUsername ?? 'Discord connected' : 'Connect your Discord account'}</strong><small>{user.discordLinked ? 'You can use Discord to sign in to this panel account.' : 'Authorize Discord once, then use it for future panel logins.'}</small></span>{user.discordLinked?<button className="danger" onClick={async()=>{try{await api('/auth/discord/link',{method:'DELETE'});location.reload()}catch(e){onError(e instanceof Error?e.message:'Unable to disconnect Discord')}}}>DISCONNECT</button>:<a className="discord-login" href="/api/auth/discord/link">CONNECT DISCORD</a>}</div></div>
    <div className="active-sessions"><span className="eyebrow">ACTIVE SESSIONS</span>{sessions.map(item=><div className="session-row" key={item.id}><div><strong>{item.current?'This device':'Signed-in device'}</strong><small>{item.ipAddress} · {new Date(item.lastSeenAt).toLocaleString()}</small><p>{item.userAgent}</p></div><button disabled={item.current} onClick={async()=>{if(await confirmation.ask('Revoke session?','This device will be signed out on its next request.','REVOKE')){await api(`/auth/sessions/${item.id}`,{method:'DELETE'});loadSessions()}}}>REVOKE</button></div>)}</div>
  </article>
}

function DiscordDiagnosticsPanel({onError}:{onError:(message:string)=>void}){
  type Diagnostic={bot:{enabled:boolean;connected:boolean;botName?:string;error?:string};guildId:string;guildFound:boolean;guildName?:string;channels:Array<{purpose:string;channelId:string;found:boolean;name?:string;canView:boolean;canSend:boolean}>;issues:string[]}
  const [data,setData]=useState<Diagnostic|null>(null)
  const load=()=>api<Diagnostic>('/integrations/discord/diagnostics').then(setData).catch(e=>onError(e.message))
  useEffect(()=>{void load()},[])
  return <article className="panel settings-section"><Bot size={26}/><div className="panel-head"><div><h2>Discord diagnostics</h2><p>Live validation of the bot, guild, and delivery channels.</p></div><button onClick={load}><RefreshCw size={14}/> RUN CHECKS</button></div>{data&&<><div className="diagnostic-summary"><span className={`tag ${data.bot.connected?'':'red'}`}>{data.bot.connected?'BOT CONNECTED':'BOT OFFLINE'}</span><strong>{data.guildFound?data.guildName:'Guild not found'}</strong></div>{data.channels.map(x=><div className="diagnostic-row" key={x.purpose}><strong>{x.purpose}</strong><span>{x.found?`#${x.name}`:x.channelId||'Not configured'}</span><span className={`tag ${x.canView&&x.canSend?'':'red'}`}>{x.canView&&x.canSend?'READY':'CHECK ACCESS'}</span></div>)}{data.issues.map(issue=><p className="error" key={issue}>{issue}</p>)}</>}</article>
}

function UpdateCenterPanel({onError}:{onError:(message:string)=>void}){
  const [data,setData]=useState<Record<string,string>|null>(null)
  useEffect(()=>{api<Record<string,string>>('/system/versions').then(setData).catch(e=>onError(e.message))},[])
  return <article className="panel settings-section"><RefreshCw size={26}/><h2>Update center</h2><p>Installed control-plane and runtime versions. Back up each server before applying updates.</p>{data&&Object.entries(data).map(([key,value])=><div className="setting-info-row" key={key}><strong>{key.replace(/([A-Z])/g,' $1')}</strong><code>{String(value)}</code></div>)}</article>
}

function NotificationHistoryPanel({onError}:{onError:(message:string)=>void}) {
  type Delivery={id:string;at:string;category:string;severity:string;title:string;channelId:string;status:string;attempts:number;error?:string}
  const [items,setItems]=useState<Delivery[]>([])
  const load=()=>api<Delivery[]>('/integrations/notifications/history?take=50').then(setItems).catch(e=>onError(e.message))
  useEffect(()=>{void load()},[])
  return <article className="panel settings-alerts notification-history"><Activity size={26}/><div className="panel-head"><div><h2>Notification delivery</h2><p>Recent Discord deliveries, retry counts, and failures.</p></div><button onClick={load}><RefreshCw size={13}/> REFRESH</button></div><div className="notification-list">{items.map(item=><div key={item.id}><span className={`tag ${item.status==='failed'?'red':''}`}>{item.status}</span><strong>{item.title}</strong><small>{item.category} · {new Date(item.at).toLocaleString()} · {item.attempts} attempt{item.attempts===1?'':'s'}</small>{item.error&&<code>{item.error}</code>}</div>)}</div>{!items.length&&<EmptyMini text="No Discord notifications have been attempted yet."/>}</article>
}

const discordPermissions = [
  ['view','View server'],['server.start','Start server'],['server.stop','Stop server'],
  ['server.restart','Restart server'],['announcements','Announcements'],['players','View players'],
  ['players.history','Player profiles'],['players.notes','Player notes'],['players.actions','Player flags'],
  ['players.mute','Mute players'],['players.kick','Kick players'],['players.ban','Ban players'],
] as const
const gameRaPermissions = [
  'KickingAndShortTermBanning','BanningUpToDay','LongTermBanning','ForceclassSelf',
  'ForceclassToSpectator','ForceclassWithoutRestrictions','GivingItems','WarheadEvents',
  'RespawnEvents','RoundEvents','SetGroup','GameplayData','Overwatch','FacilityManagement',
  'PlayersManagement','PermissionsManagement','ServerConsoleCommands','ViewHiddenBadges','ServerConfigs',
  'Broadcasting','PlayerSensitiveDataAccess','Noclip','AFKImmunity','AdminChat',
  'ViewHiddenGlobalBadges','Announcer','Effects','FriendlyFireDetectorImmunity',
  'FriendlyFireDetectorTempDisable','ServerLogLiveFeed','ExecuteAs','Vanish',
] as const
const gameBadgeColors=['none','pink','red','brown','silver','light_green','crimson','cyan',
  'aqua','deep_pink','tomato','yellow','magenta','blue_green','orange','lime','green',
  'emerald','carmine','nickel','mint','army_green','pumpkin'] as const

function IngamePermissionsPage({servers,onError}:{servers:Server[];onError:(message:string)=>void}) {
  type Health={issues:Array<{severity:string;code:string;message:string;serverId?:string;groupName?:string}>;roles:Array<{serverId:string;serverName:string;groupName:string;priority:number;onlinePlayers:number;bridgeConnected:boolean;nativePermissionCount:number;pluginPermissionCount:number}>;nativePermissionCatalog:string[];pluginPermissionCatalog:string[]}
  type Diagnostic={serverName:string;userId:string;displayName?:string;online:boolean;currentGameRole?:string;assignment:{assigned:boolean;groupName?:string;discordRoleId?:string;permissions?:string[];pluginPermissions?:string[]};inheritedGroups:string[];issues:Health['issues']}
  type NativeComparison={found:boolean;path?:string;nativeGroups:string[];panelGroups:string[];nativeMembers:string[];issues:Health['issues']}
  const [settings,setSettings]=useState<IntegrationSettings|null>(null)
  const [persistedSettings,setPersistedSettings]=useState<IntegrationSettings|null>(null)
  const [guildRoles,setGuildRoles]=useState<Array<{id:string;name:string;position:number;color:number}>>([])
  const [roleModal,setRoleModal]=useState<{index:number;view:boolean}|null>(null)
  const [health,setHealth]=useState<Health|null>(null)
  const [diagnostic,setDiagnostic]=useState<Diagnostic|null>(null)
  const [nativeComparison,setNativeComparison]=useState<NativeComparison|null>(null)
  const [diagnosticServer,setDiagnosticServer]=useState('')
  const [diagnosticUser,setDiagnosticUser]=useState('')
  const [syncing,setSyncing]=useState(false)
  const confirmation=useConfirmDialog()
  const load=useCallback(()=>api<IntegrationSettings>('/integrations').then(value=>{
    const loaded={...defaultIntegration,...value,discordGameRoleGrants:value.discordGameRoleGrants??[]}
    setSettings(loaded);setPersistedSettings(loaded)
  }).catch(error=>onError(error.message)),[onError])
  const loadHealth=useCallback(()=>api<Health>('/permissions/health').then(setHealth).catch(error=>onError(error.message)),[onError])
  useEffect(()=>{void load();void loadHealth();api<typeof guildRoles>('/integrations/discord/roles').then(setGuildRoles).catch(error=>onError(error.message))},[load,loadHealth,onError])
  if(!settings)return <Skeleton/>
  const roles=settings.discordGameRoleGrants??[]
  const update=(index:number,patch:Partial<IntegrationSettings['discordGameRoleGrants'][number]>)=>setSettings({...settings,discordGameRoleGrants:roles.map((role,i)=>i===index?{...role,...patch}:role)})
  const applyRoleTemplate=(index:number,template:'owner'|'admin'|'moderator')=>{
    const permissions=template==='owner'?[...gameRaPermissions]:template==='admin'
      ? gameRaPermissions.filter(permission=>!['PermissionsManagement','ServerConfigs','ExecuteAs'].includes(permission))
      : gameRaPermissions.filter(permission=>['KickingAndShortTermBanning','BanningUpToDay','ForceclassSelf','ForceclassToSpectator','WarheadEvents','RoundEvents','Overwatch','FacilityManagement','ViewHiddenBadges','AdminChat'].includes(permission))
    update(index,{permissions,kickPower:template==='owner'?255:template==='admin'?100:50,requiredKickPower:template==='owner'?255:template==='admin'?101:51,badgeText:template.toUpperCase(),badgeColor:template==='owner'?'red':template==='admin'?'blue':'silver'})
  }
  const add=()=>{setSettings({...settings,discordGameRoleGrants:[...roles,{roleId:'',serverId:servers[0]?.id??'',groupName:'',priority:0,enabled:true,permissions:[],inheritedGroups:[],badgeText:'',badgeColor:'silver',hidden:false,cover:true,reservedSlot:false,kickPower:0,requiredKickPower:0,pluginPermissions:[]}]});setRoleModal({index:roles.length,view:false})}
  const duplicate=(index:number)=>{const role=roles[index];setSettings({...settings,discordGameRoleGrants:[...roles,{...role,groupName:`${role.groupName||'role'}-copy`,badgeText:`${role.badgeText||role.groupName||'Role'} Copy`,permissions:[...(role.permissions??[])],inheritedGroups:[...(role.inheritedGroups??[])],pluginPermissions:[...(role.pluginPermissions??[])]}]});setRoleModal({index:roles.length,view:false})}
  const remove=async(index:number)=>{
    if(!await confirmation.ask('Delete in-game role?',`Permanently remove ${roles[index].groupName||'this role'}?`,'DELETE ROLE',true))return
    const next={...settings,discordGameRoleGrants:roles.filter((_,i)=>i!==index)}
    try{
      await api('/integrations',{method:'PUT',body:JSON.stringify(next)})
      setSettings(next)
      setPersistedSettings(next)
      setRoleModal(null)
      void loadHealth()
      window.dispatchEvent(new CustomEvent('panel-success',{detail:'In-game role deleted'}))
    }catch(error){onError(error instanceof Error?error.message:'Unable to delete role')}
  }
  const move=async(index:number,direction:-1|1)=>{
    const target=index+direction
    if(target<0||target>=roles.length)return
    const reordered=[...roles]
    ;[reordered[index],reordered[target]]=[reordered[target],reordered[index]]
    const next={...settings,discordGameRoleGrants:reordered}
    try{
      await api('/integrations',{method:'PUT',body:JSON.stringify(next)})
      setSettings(next)
      setPersistedSettings(next)
      void loadHealth()
      window.dispatchEvent(new CustomEvent('panel-success',{detail:'Role order updated'}))
    }catch(error){onError(error instanceof Error?error.message:'Unable to reorder roles')}
  }
  const syncAll=async()=>{
    setSyncing(true)
    try{
      const connectedIds=[...new Set((health?.roles??[]).filter(role=>role.bridgeConnected).map(role=>role.serverId))]
      for(const serverId of connectedIds)await api(`/permissions/sync/${serverId}`,{method:'POST'})
      await loadHealth()
      window.dispatchEvent(new CustomEvent('panel-success',{detail:'Online player permissions synchronized'}))
    }catch(error){onError(error instanceof Error?error.message:'Unable to synchronize permissions')}
    finally{setSyncing(false)}
  }
  const diagnose=async()=>{
    if(!diagnosticServer||!diagnosticUser.trim())return
    try{setDiagnostic(await api<Diagnostic>(`/permissions/diagnose/${diagnosticServer}?userId=${encodeURIComponent(diagnosticUser.trim())}`))}
    catch(error){onError(error instanceof Error?error.message:'Unable to diagnose player permissions')}
  }
  const closeEditor=()=>{if(persistedSettings)setSettings(persistedSettings);setRoleModal(null)}
  const compareNative=async()=>{
    if(!diagnosticServer)return
    try{setNativeComparison(await api<NativeComparison>(`/permissions/native/${diagnosticServer}`))}
    catch(error){onError(error instanceof Error?error.message:'Unable to compare native RA configuration')}
  }
  return <><PageTitle eyebrow="ACCESS CONTROL" title="In-game Permissions"><button className="primary" onClick={add}><Plus size={15}/> ADD ROLE</button></PageTitle>
    <section className="panel ingame-permissions-intro"><Shield size={24}/><div><h2>Discord-synchronized Remote Admin</h2><p>Define complete runtime SCP:SL roles. The highest-priority matching Discord role is applied when a linked player joins.</p></div><span className="tag">{roles.length} ROLE{roles.length===1?'':'S'}</span></section>
    <section className="permission-operations-grid"><article className="panel permission-health-panel"><div className="panel-head"><div><span className="eyebrow">CONFIGURATION HEALTH</span><h2>{health?.issues.length??0} issue{health?.issues.length===1?'':'s'} detected</h2></div><button onClick={()=>void loadHealth()}><RefreshCw size={14}/> REFRESH</button></div>{health?.issues.length?<div className="permission-issue-list">{health.issues.map((issue,index)=><div className={`permission-issue ${issue.severity}`} key={`${issue.code}-${index}`}><span>{issue.severity.toUpperCase()}</span><div><strong>{issue.code}</strong><p>{issue.message}</p></div></div>)}</div>:<p className="permission-ok">No role conflicts or validation problems detected.</p>}</article><article className="panel permission-diagnostic-panel"><div className="panel-head"><div><span className="eyebrow">PLAYER DIAGNOSTICS</span><h2>Resolve effective access</h2></div><button disabled={syncing} onClick={()=>void syncAll()}><RefreshCw size={14}/> {syncing?'SYNCING…':'SYNC ONLINE'}</button></div><div className="diagnostic-controls"><select value={diagnosticServer} onChange={e=>{setDiagnosticServer(e.target.value);setNativeComparison(null)}}><option value="">Select server…</option>{servers.map(server=><option value={server.id} key={server.id}>{server.name}</option>)}</select><input value={diagnosticUser} onChange={e=>setDiagnosticUser(e.target.value)} placeholder="Steam ID, user ID, or online player ID"/><button className="primary" onClick={()=>void diagnose()}>DIAGNOSE</button><button onClick={()=>void compareNative()}>COMPARE NATIVE RA</button></div>{diagnostic&&<div className="diagnostic-result"><div><span>MATCH</span><strong>{diagnostic.assignment.assigned?diagnostic.assignment.groupName:'No matching role'}</strong></div><div><span>PLAYER</span><strong>{diagnostic.displayName||diagnostic.userId}</strong></div><div><span>NATIVE PERMS</span><strong>{diagnostic.assignment.permissions?.length??0}</strong></div><div><span>PLUGIN PERMS</span><strong>{diagnostic.assignment.pluginPermissions?.length??0}</strong></div></div>}{nativeComparison&&<div className="native-comparison"><strong>{nativeComparison.found?'Native RA configuration found':'Native RA configuration not found'}</strong><p>{nativeComparison.path||'Check the server query-port configuration directory.'}</p><span>Native groups: {nativeComparison.nativeGroups.join(', ')||'none'}</span><span>Panel groups: {nativeComparison.panelGroups.join(', ')||'none'}</span><span>Static members: {nativeComparison.nativeMembers.length}</span></div>}</article></section>
    <form className="ingame-permissions-page" onSubmit={async event=>{event.preventDefault();try{await api('/integrations',{method:'PUT',body:JSON.stringify(settings)});setPersistedSettings(settings);window.dispatchEvent(new CustomEvent('panel-success',{detail:'In-game permissions saved'}));setRoleModal(null);void loadHealth()}catch(error){onError(error instanceof Error?error.message:'Unable to save roles')}}}>
      {!!roles.length&&<div className="ingame-role-grid">{roles.map((role,index)=><article className={`panel ingame-role-card ${role.enabled?'':'disabled'}`} key={`card-${index}`}><header><div className="role-badge-preview">{(role.badgeText||role.groupName||'?').slice(0,2).toUpperCase()}</div><div><span className="eyebrow">ROLE {index+1}</span><h2>{role.groupName||'Unnamed role'}</h2><p>{guildRoles.find(item=>item.id===role.roleId)?.name||role.roleId||'No Discord role selected'}</p></div><span className={`tag ${role.enabled?'success':''}`}>{role.enabled?'ENABLED':'DISABLED'}</span></header><div className="role-card-stats"><div><small>SERVER</small><strong>{servers.find(server=>server.id===role.serverId)?.name||'Unknown'}</strong></div><div><small>PERMISSIONS</small><strong>{(role.permissions??[]).length}</strong></div><div><small>PRIORITY</small><strong>{role.priority}</strong></div></div><footer><div className="role-order-actions"><button type="button" disabled={index===0} title="Move role earlier" onClick={()=>void move(index,-1)}><ArrowLeft size={14}/></button><button type="button" disabled={index===roles.length-1} title="Move role later" onClick={()=>void move(index,1)}><ChevronRight size={14}/></button></div><button type="button" onClick={()=>setRoleModal({index,view:true})}><Eye size={14}/> VIEW</button><button type="button" className="edit" onClick={()=>setRoleModal({index,view:false})}><Pencil size={14}/> EDIT</button><button type="button" onClick={()=>duplicate(index)}><Copy size={14}/> DUPLICATE</button><button type="button" className="danger" onClick={()=>void remove(index)}><Trash2 size={14}/> DELETE</button></footer></article>)}</div>}
      <datalist id="plugin-permission-catalog">{health?.pluginPermissionCatalog.map(permission=><option value={permission} key={permission}/>)}</datalist>
      {roles.map((role,index)=><article className={`panel ingame-role-editor ${roleModal?.index===index?'open':''} ${roleModal?.view?'readonly':''}`} key={index}><header><div><span className="eyebrow">{roleModal?.view?'ROLE DETAILS':`EDIT ROLE ${index+1}`}</span><h2>{role.groupName||'New in-game role'}</h2><p>Discord role {role.roleId||'not selected'} · Priority {role.priority}</p></div><div><label className="check-row"><input type="checkbox" checked={role.enabled} onChange={e=>update(index,{enabled:e.target.checked})}/> Enabled</label>{roleModal?.view&&<button type="button" className="edit" onClick={()=>setRoleModal({index,view:false})}><Pencil size={14}/> EDIT</button>}<button type="button" className="icon-button" onClick={closeEditor}><X size={18}/></button></div></header>
        <div className="form-row"><label>DISCORD ROLE<select value={role.roleId} onChange={e=>{const selected=guildRoles.find(item=>item.id===e.target.value);update(index,{roleId:e.target.value,...(!role.groupName&&selected?{groupName:selected.name.toLowerCase().replace(/[^a-z0-9]+/g,'-')}:{ }),...(!role.badgeText&&selected?{badgeText:selected.name}:{})})}}><option value="">Select a Discord role…</option>{role.roleId&&!guildRoles.some(item=>item.id===role.roleId)&&<option value={role.roleId}>Unknown role ({role.roleId})</option>}{guildRoles.map(item=><option value={item.id} key={item.id}>{item.name} · {item.id}</option>)}</select></label><label>SERVER<select value={role.serverId} onChange={e=>update(index,{serverId:e.target.value})}>{servers.map(server=><option value={server.id} key={server.id}>{server.name}</option>)}</select></label><label>RANK NAME<input value={role.groupName} onChange={e=>update(index,{groupName:e.target.value.trim()})}/></label><label>PRIORITY<input type="number" value={role.priority} onChange={e=>update(index,{priority:Number(e.target.value)})}/></label></div>
        <div className="form-row"><label>BADGE TEXT<input value={role.badgeText??''} onChange={e=>update(index,{badgeText:e.target.value})}/></label><label>BADGE COLOR<select value={role.badgeColor??'silver'} onChange={e=>update(index,{badgeColor:e.target.value})}>{gameBadgeColors.map(color=><option value={color} key={color}>{color.replaceAll('_',' ')}</option>)}</select></label><label>KICK POWER<input type="number" min={0} max={255} value={role.kickPower??0} onChange={e=>update(index,{kickPower:Number(e.target.value)})}/></label><label>REQUIRED KICK POWER<input type="number" min={0} max={255} value={role.requiredKickPower??0} onChange={e=>update(index,{requiredKickPower:Number(e.target.value)})}/></label></div>
        <div className="permission-chip-grid game-role-flags"><label className="check-row"><input type="checkbox" checked={role.hidden??false} onChange={e=>update(index,{hidden:e.target.checked})}/> Hidden badge</label><label className="check-row"><input type="checkbox" checked={role.cover??true} onChange={e=>update(index,{cover:e.target.checked})}/> Cover global badge</label><label className="check-row"><input type="checkbox" checked={role.reservedSlot??false} onChange={e=>update(index,{reservedSlot:e.target.checked})}/> Reserved slot</label></div>
        <details className="inheritance-dropdown permission-dropdown"><summary><div><span className="eyebrow">INHERITED RANKS</span><strong>{(role.inheritedGroups??[]).length?`${(role.inheritedGroups??[]).length} selected`:'No inherited ranks'}</strong></div><span>SELECT RANKS</span></summary><div className="inheritance-options">{roles.map((candidate,candidateIndex)=>candidateIndex===index||!candidate.groupName?null:<label className="check-row" key={`${candidate.groupName}-${candidateIndex}`}><input type="checkbox" checked={(role.inheritedGroups??[]).includes(candidate.groupName)} onChange={()=>{const selected=role.inheritedGroups??[];update(index,{inheritedGroups:selected.includes(candidate.groupName)?selected.filter(name=>name!==candidate.groupName):[...selected,candidate.groupName]})}}/><span><strong>{candidate.groupName}</strong><small>{candidate.badgeText||'No badge text'}</small></span></label>)}</div></details>
        <details className="game-permission-selector permission-dropdown"><summary><div><span className="eyebrow">REMOTE ADMIN PERMISSIONS</span><strong>{(role.permissions??[]).length} of {gameRaPermissions.length} selected</strong></div><span>SELECT PERMISSIONS</span></summary><div className="permission-dropdown-actions"><button type="button" onClick={()=>applyRoleTemplate(index,'owner')}>OWNER TEMPLATE</button><button type="button" onClick={()=>applyRoleTemplate(index,'admin')}>ADMIN TEMPLATE</button><button type="button" onClick={()=>applyRoleTemplate(index,'moderator')}>MOD TEMPLATE</button><button type="button" onClick={()=>update(index,{permissions:[...gameRaPermissions]})}>SELECT ALL</button><button type="button" onClick={()=>update(index,{permissions:[]})}>CLEAR ALL</button></div><div className="permission-chip-grid">{gameRaPermissions.map(permission=>{const selected=role.permissions??[];return <label className="check-row" key={permission}><input type="checkbox" checked={selected.includes(permission)} onChange={()=>update(index,{permissions:selected.includes(permission)?selected.filter(x=>x!==permission):[...selected,permission]})}/>{permission}</label>})}</div></details>
        <div className="multi-value-editor"><div className="multi-value-head"><div><span className="eyebrow">LABAPI PLUGIN PERMISSIONS</span><small>Exact permissions and wildcards such as at.* are supplied through the bridge's shared LabAPI permission provider.</small></div><button type="button" onClick={()=>update(index,{pluginPermissions:[...(role.pluginPermissions??[]),'']})}><Plus size={13}/> ADD PERMISSION</button></div>{!(role.pluginPermissions??[]).length&&<div className="multi-value-empty">No custom plugin permissions configured.</div>}{(role.pluginPermissions??[]).map((permission,permissionIndex)=><div className="multi-value-row" key={permissionIndex}><input list="plugin-permission-catalog" value={permission} onChange={e=>update(index,{pluginPermissions:(role.pluginPermissions??[]).map((value,i)=>i===permissionIndex?e.target.value:value)})} placeholder="at.customclass"/><button type="button" className="danger" aria-label="Remove permission" onClick={()=>update(index,{pluginPermissions:(role.pluginPermissions??[]).filter((_,i)=>i!==permissionIndex)})}><Trash2 size={14}/></button></div>)}</div>
        <footer className="ingame-role-modal-actions"><span>{roleModal?.view?'Viewing saved role configuration':'Cancel discards every unsaved role change'}</span><div><button type="button" onClick={closeEditor}>{roleModal?.view?'CLOSE':'CANCEL'}</button>{!roleModal?.view&&<button className="primary"><Save size={15}/> SAVE ROLE</button>}</div></footer>
      </article>)}
      {!roles.length&&<div className="empty-page"><Shield/><h2>No in-game roles</h2><p>Add a role to synchronize Discord staff access with SCP:SL Remote Admin.</p><button type="button" className="primary" onClick={add}>ADD FIRST ROLE</button></div>}
    </form>
    {confirmation.dialog}
  </>
}

function DonorManagementPageV2({servers,onError}:{servers:Server[];onError:(e:string)=>void}){
  type Editor={kind:'donor'|'badge'|'roleBadge';index:number;creating:boolean}
  const [settings,setSettings]=useState<IntegrationSettings|null>(null)
  const [persisted,setPersisted]=useState<IntegrationSettings|null>(null)
  const [roles,setRoles]=useState<{id:string;name:string;position:number;color:number}[]>([])
  const [players,setPlayers]=useState<{serverId:string;serverName:string;player:StoredPlayer}[]>([])
  const [editor,setEditor]=useState<Editor|null>(null)
  const [busy,setBusy]=useState(false)
  const confirmation=useConfirmDialog()
  useEffect(()=>{
    api<IntegrationSettings>('/integrations/donors-badges').then(value=>{const loaded={...defaultIntegration,...value,discordDonorRoleGrants:value.discordDonorRoleGrants??[],customUserBadges:value.customUserBadges??[],customRoleBadges:value.customRoleBadges??[]};setSettings(loaded);setPersisted(loaded)}).catch(error=>onError(error.message))
    api<typeof roles>('/integrations/discord/roles').then(setRoles).catch(error=>onError(error.message))
    api<typeof players>('/players/global').then(setPlayers).catch(error=>onError(error.message))
  },[onError])
  if(!settings)return <Skeleton/>
  const grants=settings.discordDonorRoleGrants??[]
  const badges=settings.customUserBadges??[]
  const roleBadges=settings.customRoleBadges??[]
  const updateGrant=(index:number,patch:Partial<IntegrationSettings['discordDonorRoleGrants'][number]>)=>setSettings({...settings,discordDonorRoleGrants:grants.map((item,i)=>i===index?{...item,...patch}:item)})
  const updateBadge=(index:number,patch:Partial<IntegrationSettings['customUserBadges'][number]>)=>setSettings({...settings,customUserBadges:badges.map((item,i)=>i===index?{...item,...patch}:item)})
  const updateRoleBadge=(index:number,patch:Partial<IntegrationSettings['customRoleBadges'][number]>)=>setSettings({...settings,customRoleBadges:roleBadges.map((item,i)=>i===index?{...item,...patch}:item)})
  const createDonor=()=>{setSettings({...settings,discordDonorRoleGrants:[...grants,{roleId:'',serverId:servers[0]?.id??'',tier:1,priority:0,enabled:true}]});setEditor({kind:'donor',index:grants.length,creating:true})}
  const createBadge=()=>{setSettings({...settings,customUserBadges:[...badges,{serverId:servers[0]?.id??'',steamId:'',badgeText:'',badgeColor:'silver'}]});setEditor({kind:'badge',index:badges.length,creating:true})}
  const createRoleBadge=()=>{setSettings({...settings,customRoleBadges:[...roleBadges,{roleId:'',serverId:servers[0]?.id??'',badgeText:'',badgeColor:'silver',priority:0,enabled:true}]});setEditor({kind:'roleBadge',index:roleBadges.length,creating:true})}
  const close=()=>{if(persisted)setSettings(persisted);setEditor(null)}
  const save=async(sync=false)=>{
    setBusy(true)
    try{await api('/integrations/donors-badges',{method:'PUT',body:JSON.stringify(settings)});setPersisted(settings);setEditor(null);let message='Donor settings saved';if(sync){const result=await api<{donors:number}[]>('/integrations/discord/donors/sync',{method:'POST'});message=`Synchronized ${result.reduce((sum,item)=>sum+item.donors,0)} donor rows`}window.dispatchEvent(new CustomEvent('panel-success',{detail:message}))}
    catch(error){onError(error instanceof Error?error.message:'Unable to save donor settings')}finally{setBusy(false)}
  }
  const remove=async(kind:'donor'|'badge'|'roleBadge',index:number)=>{
    if(!await confirmation.ask(kind==='donor'?'Delete donor mapping?':kind==='roleBadge'?'Delete custom role badge?':'Delete custom user badge?','This item will be removed immediately.','DELETE',true))return
    const next=kind==='donor'?{...settings,discordDonorRoleGrants:grants.filter((_,i)=>i!==index)}:kind==='badge'?{...settings,customUserBadges:badges.filter((_,i)=>i!==index)}:{...settings,customRoleBadges:roleBadges.filter((_,i)=>i!==index)}
    try{await api('/integrations/donors-badges',{method:'PUT',body:JSON.stringify(next)});setSettings(next);setPersisted(next);window.dispatchEvent(new CustomEvent('panel-success',{detail:'Item deleted'}))}catch(error){onError(error instanceof Error?error.message:'Unable to delete item')}
  }
  const grant=editor?.kind==='donor'?grants[editor.index]:null
  const badge=editor?.kind==='badge'?badges[editor.index]:null
  const roleBadge=editor?.kind==='roleBadge'?roleBadges[editor.index]:null
  const availablePlayers=badge?players.filter(record=>record.serverId===badge.serverId&&record.player.discordId):[]
  const roleBadgeModal=roleBadge&&<div className="modal-backdrop donor-modal-backdrop"><form className="modal donor-editor-modal" onSubmit={event=>{event.preventDefault();void save(false)}}><div className="modal-head"><div><span className="eyebrow">{editor?.creating?'CREATE':'EDIT'} ROLE BADGE</span><h2>Configure custom role badge</h2><p>Choose a Discord role, badge appearance, and matching priority.</p></div><button type="button" className="icon-button" onClick={close}><X size={18}/></button></div><div className="donor-modal-body"><label className="check-row"><input type="checkbox" checked={roleBadge.enabled} onChange={event=>updateRoleBadge(editor!.index,{enabled:event.target.checked})}/> Badge enabled</label><div className="form-row"><label>DISCORD ROLE<select required value={roleBadge.roleId} onChange={event=>updateRoleBadge(editor!.index,{roleId:event.target.value})}><option value="">Select a Discord role…</option>{roleBadge.roleId&&!roles.some(role=>role.id===roleBadge.roleId)&&<option value={roleBadge.roleId}>Unknown role ({roleBadge.roleId})</option>}{roles.map(role=><option value={role.id} key={role.id}>{role.name} · {role.id}</option>)}</select></label><label>SERVER<select required value={roleBadge.serverId} onChange={event=>updateRoleBadge(editor!.index,{serverId:event.target.value})}>{servers.map(server=><option value={server.id} key={server.id}>{server.name}</option>)}</select></label><label>BADGE TEXT<input required maxLength={64} value={roleBadge.badgeText} onChange={event=>updateRoleBadge(editor!.index,{badgeText:event.target.value})} placeholder="SUPPORTER"/></label><label>BADGE COLOR<select required value={roleBadge.badgeColor} onChange={event=>updateRoleBadge(editor!.index,{badgeColor:event.target.value})}>{gameBadgeColors.map(color=><option value={color} key={color}>{color.replaceAll('_',' ')}</option>)}</select></label><label>PRIORITY<input required type="number" value={roleBadge.priority} onChange={event=>updateRoleBadge(editor!.index,{priority:Number(event.target.value)})}/></label></div></div><div className="modal-actions"><button type="button" onClick={close}>CANCEL</button><button className="primary" disabled={busy}><Save size={15}/> {busy?'SAVING…':editor?.creating?'CREATE':'SAVE CHANGES'}</button></div></form></div>
  return <><PageTitle eyebrow="DISCORD INTEGRATION" title="Donors & Custom Badges"><button disabled={busy} onClick={()=>void save(true)}><RefreshCw size={15}/> SYNC NOW</button></PageTitle>
    <article className="panel ingame-permissions-intro"><Users size={28}/><div><h2>Discord-synchronized donors</h2><p>Map Discord roles to donor tiers and assign live bridge-managed badges to linked players.</p></div><span className="tag">{grants.length} MAPPINGS</span></article>
    <section className="donor-section-head"><div><span className="eyebrow">DONOR ACCESS</span><h2>Discord donor roles</h2></div><button className="primary" onClick={createDonor}><Plus size={15}/> CREATE MAPPING</button></section>
    <section className="ingame-permissions-page">{!!grants.length&&<div className="ingame-role-grid">{grants.map((item,index)=>{const role=roles.find(value=>value.id===item.roleId);return <article className={`panel ingame-role-card ${item.enabled?'':'disabled'}`} key={index}><header><div className="role-badge-preview">T{item.tier}</div><div><span className="eyebrow">DONOR MAPPING {index+1}</span><h2>{role?.name??'Unassigned Discord role'}</h2><p>{servers.find(server=>server.id===item.serverId)?.name??'Unknown server'}</p></div><span className={`tag ${item.enabled?'success':''}`}>{item.enabled?'ENABLED':'DISABLED'}</span></header><div className="role-card-stats"><div><small>TIER</small><strong>{item.tier}</strong></div><div><small>PRIORITY</small><strong>{item.priority}</strong></div><div><small>ROLE</small><strong>{item.roleId||'—'}</strong></div></div><footer><button className="edit" onClick={()=>setEditor({kind:'donor',index,creating:false})}><Pencil size={14}/> EDIT</button><button className="danger" onClick={()=>void remove('donor',index)}><Trash2 size={14}/> DELETE</button></footer></article>})}</div>}{!grants.length&&<div className="empty-page"><Users/><h2>No donor mappings</h2><p>Create a mapping to begin synchronizing donor roles.</p><button className="primary" onClick={createDonor}>CREATE FIRST MAPPING</button></div>}</section>
    <section className="donor-section-head"><div><span className="eyebrow">ROLE COSMETICS</span><h2>Custom role badges</h2></div><button className="primary" onClick={createRoleBadge}><Plus size={15}/> CREATE ROLE BADGE</button></section>
    <section className="ingame-permissions-page">{!!roleBadges.length&&<div className="ingame-role-grid">{roleBadges.map((item,index)=>{const role=roles.find(value=>value.id===item.roleId);return <article className={`panel ingame-role-card ${item.enabled?'':'disabled'}`} key={index}><header><div className="role-badge-preview">{(item.badgeText||'?').slice(0,2).toUpperCase()}</div><div><span className="eyebrow">ROLE BADGE {index+1}</span><h2>{item.badgeText||'Unnamed badge'}</h2><p>{role?.name??item.roleId??'No Discord role selected'}</p></div><span className={`tag ${item.enabled?'success':''}`}>{item.enabled?'ENABLED':'DISABLED'}</span></header><div className="role-card-stats"><div><small>SERVER</small><strong>{servers.find(server=>server.id===item.serverId)?.name??'Unknown'}</strong></div><div><small>PRIORITY</small><strong>{item.priority}</strong></div><div><small>COLOR</small><strong>{item.badgeColor}</strong></div></div><footer><button className="edit" onClick={()=>setEditor({kind:'roleBadge',index,creating:false})}><Pencil size={14}/> EDIT</button><button className="danger" onClick={()=>void remove('roleBadge',index)}><Trash2 size={14}/> DELETE</button></footer></article>})}</div>}{!roleBadges.length&&<div className="empty-page"><Shield/><h2>No custom role badges</h2><p>Assign a badge to every linked player with a Discord role.</p><button className="primary" onClick={createRoleBadge}>CREATE FIRST ROLE BADGE</button></div>}</section>
    <section className="donor-section-head"><div><span className="eyebrow">PLAYER COSMETICS</span><h2>Custom user badges</h2></div><button className="primary" onClick={createBadge}><Plus size={15}/> CREATE BADGE</button></section>
    <section className="ingame-permissions-page">{!!badges.length&&<div className="ingame-role-grid">{badges.map((item,index)=>{const linked=players.find(record=>record.serverId===item.serverId&&record.player.userId.split('@')[0]===item.steamId);return <article className="panel ingame-role-card" key={index}><header><div className="role-badge-preview">{(item.badgeText||'?').slice(0,2).toUpperCase()}</div><div><span className="eyebrow">USER BADGE {index+1}</span><h2>{item.badgeText||'Unnamed badge'}</h2><p>{linked?.player.discordDisplayName??linked?.player.currentName??item.steamId}</p></div><span className="tag">{item.badgeColor.replaceAll('_',' ').toUpperCase()}</span></header><div className="role-card-stats"><div><small>SERVER</small><strong>{servers.find(server=>server.id===item.serverId)?.name??'Unknown'}</strong></div><div><small>STEAM ID</small><strong>{item.steamId||'—'}</strong></div><div><small>COLOR</small><strong>{item.badgeColor}</strong></div></div><footer><button className="edit" onClick={()=>setEditor({kind:'badge',index,creating:false})}><Pencil size={14}/> EDIT</button><button className="danger" onClick={()=>void remove('badge',index)}><Trash2 size={14}/> DELETE</button></footer></article>})}</div>}{!badges.length&&<div className="empty-page"><Shield/><h2>No custom badges</h2><p>Create a bridge-managed badge for a linked player.</p><button className="primary" onClick={createBadge}>CREATE FIRST BADGE</button></div>}</section>
    {editor&&<div className="modal-backdrop donor-modal-backdrop" onMouseDown={event=>event.target===event.currentTarget&&close()}><form className="modal donor-editor-modal" onSubmit={event=>{event.preventDefault();void save(false)}}><div className="modal-head"><div><span className="eyebrow">{editor.creating?'CREATE':'EDIT'} {editor.kind==='donor'?'DONOR MAPPING':'USER BADGE'}</span><h2>{editor.kind==='donor'?'Configure donor role':'Configure custom badge'}</h2><p>{editor.kind==='donor'?'Choose a Discord role, server, donor tier, and priority.':'Choose a linked user, badge text, and badge color.'}</p></div><button type="button" className="icon-button" onClick={close}><X size={18}/></button></div><div className="donor-modal-body">
      {grant&&<><label className="check-row"><input type="checkbox" checked={grant.enabled} onChange={event=>updateGrant(editor.index,{enabled:event.target.checked})}/> Mapping enabled</label><div className="form-row"><label>DISCORD ROLE<select required value={grant.roleId} onChange={event=>updateGrant(editor.index,{roleId:event.target.value})}><option value="">Select a Discord role…</option>{grant.roleId&&!roles.some(role=>role.id===grant.roleId)&&<option value={grant.roleId}>Unknown role ({grant.roleId})</option>}{roles.map(role=><option value={role.id} key={role.id}>{role.name} · {role.id}</option>)}</select></label><label>SERVER<select required value={grant.serverId} onChange={event=>updateGrant(editor.index,{serverId:event.target.value})}>{servers.map(server=><option value={server.id} key={server.id}>{server.name}</option>)}</select></label><label>DONOR TIER<input required type="number" min={1} value={grant.tier} onChange={event=>updateGrant(editor.index,{tier:Number(event.target.value)})}/></label><label>PRIORITY<input required type="number" value={grant.priority} onChange={event=>updateGrant(editor.index,{priority:Number(event.target.value)})}/></label></div></>}
      {badge&&<div className="form-row"><label>SERVER<select required value={badge.serverId} onChange={event=>updateBadge(editor.index,{serverId:event.target.value,steamId:''})}>{servers.map(server=><option value={server.id} key={server.id}>{server.name}</option>)}</select></label><label>LINKED USER<select required value={badge.steamId} onChange={event=>updateBadge(editor.index,{steamId:event.target.value})}><option value="">Select a linked user…</option>{badge.steamId&&!availablePlayers.some(record=>record.player.userId.split('@')[0]===badge.steamId)&&<option value={badge.steamId}>{badge.steamId}</option>}{availablePlayers.map(record=>{const steam=record.player.userId.split('@')[0];return <option value={steam} key={record.player.id}>{record.player.discordDisplayName??record.player.currentName} · {steam}</option>})}</select></label><label>BADGE TEXT<input required maxLength={64} value={badge.badgeText} onChange={event=>updateBadge(editor.index,{badgeText:event.target.value})} placeholder="SUPPORTER"/></label><label>BADGE COLOR<select required value={badge.badgeColor} onChange={event=>updateBadge(editor.index,{badgeColor:event.target.value})}>{gameBadgeColors.map(color=><option value={color} key={color}>{color.replaceAll('_',' ')}</option>)}</select></label></div>}
    </div><div className="modal-actions"><button type="button" onClick={close}>CANCEL</button><button className="primary" disabled={busy}><Save size={15}/> {busy?'SAVING…':editor.creating?'CREATE':'SAVE CHANGES'}</button></div></form></div>}
    {roleBadgeModal}
    {confirmation.dialog}
  </>
}

function DonorManagementPage({servers,onError}:{servers:Server[];onError:(e:string)=>void}){
  const [settings,setSettings]=useState<IntegrationSettings|null>(null)
  const [roles,setRoles]=useState<{id:string;name:string;position:number;color:number}[]>([])
  const [players,setPlayers]=useState<{serverId:string;serverName:string;player:StoredPlayer}[]>([])
  const [busy,setBusy]=useState(false)
  useEffect(()=>{
    api<IntegrationSettings>('/integrations').then(value=>setSettings({...defaultIntegration,...value,discordDonorRoleGrants:value.discordDonorRoleGrants??[],customUserBadges:value.customUserBadges??[]})).catch(error=>onError(error.message))
    api<typeof roles>('/integrations/discord/roles').then(setRoles).catch(error=>onError(error.message))
    api<typeof players>('/players/global').then(setPlayers).catch(error=>onError(error.message))
  },[onError])
  if(!settings)return <Skeleton/>
  const grants=settings.discordDonorRoleGrants??[]
  const update=(index:number,patch:Partial<IntegrationSettings['discordDonorRoleGrants'][number]>)=>setSettings({...settings,discordDonorRoleGrants:grants.map((grant,i)=>i===index?{...grant,...patch}:grant)})
  const badges=settings.customUserBadges??[]
  const add=()=>setSettings({...settings,discordDonorRoleGrants:[...grants,{roleId:'',serverId:servers[0]?.id??'',tier:1,priority:0,enabled:true}]})
  const addBadge=()=>setSettings({...settings,customUserBadges:[...badges,{serverId:servers[0]?.id??'',steamId:'',badgeText:'',badgeColor:'silver'}]})
  const updateBadge=(index:number,patch:Partial<IntegrationSettings['customUserBadges'][number]>)=>setSettings({...settings,customUserBadges:badges.map((badge,i)=>i===index?{...badge,...patch}:badge)})
  const save=async(sync=false)=>{
    setBusy(true)
    try{
      await api('/integrations',{method:'PUT',body:JSON.stringify(settings)})
      let message='Donor role mappings saved.'
      if(sync){
        const result=await api<{donors:number}[]>('/integrations/discord/donors/sync',{method:'POST'})
        message=`Saved and synchronized ${result.reduce((sum,item)=>sum+item.donors,0)} donor rows.`
      }
      window.dispatchEvent(new CustomEvent('panel-success',{detail:message}))
    }catch(error){onError(error instanceof Error?error.message:'Unable to save donor mappings')}
    finally{setBusy(false)}
  }
  return <><PageTitle eyebrow="DISCORD INTEGRATION" title="Donors & Custom Badges"><button onClick={()=>void save(true)} disabled={busy}><RefreshCw size={15}/> SAVE & SYNC NOW</button><button className="primary" onClick={add}><Plus size={15}/> ADD DONOR ROLE</button></PageTitle>
    <article className="panel ingame-permissions-intro"><Users size={28}/><div><h2>Role-based donor synchronization</h2><p>Discord roles update Donators.csv every five minutes. The LabAPI bridge applies custom badges live in-game.</p></div><span className="tag">{grants.length} MAPPINGS</span></article>
    <form className="ingame-permissions-page" onSubmit={event=>{event.preventDefault();void save(false)}}>
      {grants.map((grant,index)=>{const selected=roles.find(role=>role.id===grant.roleId);return <article className="panel ingame-role-editor open" key={index}><header><div><span className="eyebrow">DONOR MAPPING {index+1}</span><h2>{selected?.name??'New donor role'}</h2><p>Tier {grant.tier} · Priority {grant.priority}</p></div><div><label className="check-row"><input type="checkbox" checked={grant.enabled} onChange={event=>update(index,{enabled:event.target.checked})}/> Enabled</label><button type="button" className="danger" onClick={()=>setSettings({...settings,discordDonorRoleGrants:grants.filter((_,i)=>i!==index)})}><Trash2 size={14}/> REMOVE</button></div></header>
        <div className="form-row"><label>DISCORD ROLE<select value={grant.roleId} onChange={event=>update(index,{roleId:event.target.value})}><option value="">Select a Discord role…</option>{grant.roleId&&!roles.some(role=>role.id===grant.roleId)&&<option value={grant.roleId}>Unknown role ({grant.roleId})</option>}{roles.map(role=><option value={role.id} key={role.id}>{role.name} · {role.id}</option>)}</select></label><label>SERVER<select value={grant.serverId} onChange={event=>update(index,{serverId:event.target.value})}>{servers.map(server=><option value={server.id} key={server.id}>{server.name}</option>)}</select></label><label>DONOR TIER<input type="number" min={1} value={grant.tier} onChange={event=>update(index,{tier:Number(event.target.value)})}/></label><label>PRIORITY<input type="number" value={grant.priority} onChange={event=>update(index,{priority:Number(event.target.value)})}/></label></div>
      </article>})}
      {!grants.length&&<div className="empty-page"><Users/><h2>No donor mappings</h2><p>Add a Discord donor role to synchronize linked users, tiers, booster state, and custom badges.</p><button type="button" className="primary" onClick={add}>ADD FIRST DONOR ROLE</button></div>}
      {!!grants.length&&<div className="sticky-save-bar"><span>Save mappings before they are used by automatic synchronization.</span><button className="primary" disabled={busy}><Save size={15}/> {busy?'SAVING…':'SAVE CHANGES'}</button></div>}
    </form>
    <PageTitle eyebrow="PLAYER COSMETICS" title="Custom User Badges"><button className="primary" onClick={addBadge}><Plus size={15}/> ADD USER BADGE</button></PageTitle>
    <section className="ingame-permissions-page">
      {badges.map((badge,index)=>{const available=players.filter(record=>record.serverId===badge.serverId&&record.player.discordId);return <article className="panel ingame-role-editor open" key={index}><header><div><span className="eyebrow">USER BADGE {index+1}</span><h2>{badge.badgeText||'New custom badge'}</h2><p>{badge.steamId||'No linked user selected'} · {badge.badgeColor}</p></div><button type="button" className="danger" onClick={()=>setSettings({...settings,customUserBadges:badges.filter((_,i)=>i!==index)})}><Trash2 size={14}/> REMOVE</button></header>
        <div className="form-row"><label>SERVER<select value={badge.serverId} onChange={event=>updateBadge(index,{serverId:event.target.value,steamId:''})}>{servers.map(server=><option value={server.id} key={server.id}>{server.name}</option>)}</select></label><label>LINKED USER<select value={badge.steamId} onChange={event=>updateBadge(index,{steamId:event.target.value})}><option value="">Select a linked user…</option>{badge.steamId&&!available.some(record=>record.player.userId.split('@')[0]===badge.steamId)&&<option value={badge.steamId}>{badge.steamId}</option>}{available.map(record=>{const steam=record.player.userId.split('@')[0];return <option value={steam} key={record.player.id}>{record.player.discordDisplayName??record.player.currentName} · {steam}</option>})}</select></label><label>BADGE TEXT<input maxLength={64} value={badge.badgeText} onChange={event=>updateBadge(index,{badgeText:event.target.value})} placeholder="SUPPORTER"/></label><label>BADGE COLOR<select value={badge.badgeColor} onChange={event=>updateBadge(index,{badgeColor:event.target.value})}>{gameBadgeColors.map(color=><option value={color} key={color}>{color.replaceAll('_',' ')}</option>)}</select></label></div>
      </article>})}
      {!badges.length&&<div className="empty-page"><Shield/><h2>No custom user badges</h2><p>Add a badge to a linked Discord and Steam user.</p><button type="button" className="primary" onClick={addBadge}>ADD FIRST USER BADGE</button></div>}
      {!!badges.length&&<div className="sticky-save-bar"><span>The bridge refreshes saved badges for connected players every minute.</span><button type="button" className="primary" disabled={busy} onClick={()=>void save(false)}><Save size={15}/> {busy?'SAVING…':'SAVE BADGES'}</button></div>}
    </section>
  </>
}

function DiscordBotPanel({ servers, onError }: { servers: Server[]; onError: (e: string) => void }) {
  const [settings,setSettings] = useState<IntegrationSettings | null>(null)
  const [status,setStatus] = useState<{enabled:boolean;connected:boolean;botName:string|null;error:string|null}|null>(null)
  const loadSettings = useCallback(() =>
    api<IntegrationSettings>('/integrations').then(value=>setSettings({
      ...defaultIntegration,
      ...value,
      discordRoleGrants:value.discordRoleGrants ?? [],
      discordGameRoleGrants:value.discordGameRoleGrants ?? [],
      discordDonorRoleGrants:value.discordDonorRoleGrants ?? [],
    })).catch(e => onError(e.message)), [onError])
  const loadStatus = useCallback(() =>
    api<typeof status>('/integrations/discord/bot/status').then(setStatus).catch(e=>onError(e.message)),[onError])
  useEffect(() => {
    void loadSettings()
    void loadStatus()
    const timer=setInterval(()=>void loadStatus(),10000)
    return () => clearInterval(timer)
  }, [loadSettings,loadStatus])
  if (!settings) return null
  const addGrant=()=>setSettings({...settings,discordRoleGrants:[...(settings.discordRoleGrants ?? []),{roleId:'',serverId:servers[0]?.id ?? '',permissions:['view']}]})
  const updateGrant=(index:number,patch:Partial<IntegrationSettings['discordRoleGrants'][number]>)=>setSettings({...settings,discordRoleGrants:(settings.discordRoleGrants ?? []).map((grant,i)=>i===index?{...grant,...patch}:grant)})
  const addGameRole=()=>setSettings({...settings,discordGameRoleGrants:[...(settings.discordGameRoleGrants ?? []),{roleId:'',serverId:servers[0]?.id ?? '',groupName:'',priority:0,enabled:true,permissions:[],inheritedGroups:[],badgeText:'',badgeColor:'silver',hidden:false,cover:true,reservedSlot:false,kickPower:0,requiredKickPower:0,pluginPermissions:[]}]})
  const updateGameRole=(index:number,patch:Partial<IntegrationSettings['discordGameRoleGrants'][number]>)=>setSettings({...settings,discordGameRoleGrants:(settings.discordGameRoleGrants ?? []).map((grant,i)=>i===index?{...grant,...patch}:grant)})
  return <form className="panel settings-alerts discord-settings" onSubmit={async e => {
    e.preventDefault()
    try { await api('/integrations',{method:'PUT',body:JSON.stringify(settings)}); setTimeout(()=>void loadStatus(),1200) }
    catch(error) { onError(error instanceof Error ? error.message : 'Unable to save Discord bot settings') }
  }}>
    <Bot size={26}/><h2>Discord bot</h2>
    <p><span className={`status-dot ${status?.connected ? '' : 'off'}`}/>{status?.connected ? `Connected as ${status.botName}` : status?.error || 'Not connected'}</p>
    <label className="check-row"><input type="checkbox" checked={settings.discordBotEnabled} onChange={e=>setSettings({...settings,discordBotEnabled:e.target.checked})}/> Enable embedded Discord bot</label>
    <div className="form-row"><label>BOT TOKEN<input type="password" value={settings.discordBotToken} onChange={e=>setSettings({...settings,discordBotToken:e.target.value})}/></label><label>GUILD ID<input value={settings.discordGuildId} onChange={e=>setSettings({...settings,discordGuildId:e.target.value.trim()})}/></label><label>TECHNICAL CHANNEL ID<input value={settings.discordNotificationChannelId} onChange={e=>setSettings({...settings,discordNotificationChannelId:e.target.value.trim()})}/></label><label>MODERATION CHANNEL ID<input value={settings.discordModerationChannelId} onChange={e=>setSettings({...settings,discordModerationChannelId:e.target.value.trim()})}/></label><label>REPORT CHANNEL ID<input value={settings.discordReportChannelId} onChange={e=>setSettings({...settings,discordReportChannelId:e.target.value.trim()})}/></label><label>AUDIT CHANNEL ID<input value={settings.discordAuditChannelId} onChange={e=>setSettings({...settings,discordAuditChannelId:e.target.value.trim()})}/></label><label>FULL CONTROL ROLE IDS<input value={settings.discordControlRoleIds} onChange={e=>setSettings({...settings,discordControlRoleIds:e.target.value})} placeholder="123…, 456…"/></label><label>STEAM WEB API KEY<input type="password" value={settings.steamWebApiKey} onChange={e=>setSettings({...settings,steamWebApiKey:e.target.value})}/></label></div>
    <div className="form-row"><label className="check-row"><input type="checkbox" checked={settings.discordDailyReportEnabled} onChange={e=>setSettings({...settings,discordDailyReportEnabled:e.target.checked})}/> Send daily fleet report</label><label>REPORT HOUR (UTC)<input type="number" min={0} max={23} value={settings.discordDailyReportHourUtc} onChange={e=>setSettings({...settings,discordDailyReportHourUtc:Number(e.target.value)})}/></label></div>
    <div className="role-grant-editor"><div className="panel-head"><div><span className="eyebrow">PER-SERVER ACCESS</span><h3>Discord role permissions</h3></div><button type="button" onClick={addGrant}>ADD ROLE</button></div>
      {(settings.discordRoleGrants ?? []).map((grant,index)=><section className="role-grant-card" key={index}>
        <div className="form-row"><label>ROLE ID<input value={grant.roleId} onChange={e=>updateGrant(index,{roleId:e.target.value.trim()})}/></label><label>SERVER<select value={grant.serverId} onChange={e=>updateGrant(index,{serverId:e.target.value})}>{servers.map(server=><option value={server.id} key={server.id}>{server.name}</option>)}</select></label><button type="button" className="danger" onClick={()=>setSettings({...settings,discordRoleGrants:(settings.discordRoleGrants ?? []).filter((_,i)=>i!==index)})}>REMOVE</button></div>
        <div className="permission-chip-grid">{discordPermissions.map(([value,label])=>{const permissions=grant.permissions ?? [];return <label className="check-row" key={value}><input type="checkbox" checked={permissions.includes(value)} onChange={()=>updateGrant(index,{permissions:permissions.includes(value)?permissions.filter(x=>x!==value):[...permissions,value]})}/>{label}</label>})}</div>
      </section>)}
      {!(settings.discordRoleGrants?.length) && <p className="muted">No limited role grants. Add a role to give it specific permissions on one server.</p>}
    </div>
    <div className="role-grant-editor game-role-editor"><div className="panel-head"><div><span className="eyebrow">IN-GAME PERMISSIONS</span><h3>Discord → SCP:SL RA groups</h3><p>Linked players receive an existing Remote Admin group when they join. Higher priority wins when multiple Discord roles match.</p></div><button type="button" onClick={addGameRole}>ADD MAPPING</button></div>
      {(settings.discordGameRoleGrants ?? []).map((grant,index)=><section className="role-grant-card game-role-card" key={index}>
        <label className="check-row"><input type="checkbox" checked={grant.enabled} onChange={e=>updateGameRole(index,{enabled:e.target.checked})}/> Mapping enabled</label>
        <div className="form-row"><label>DISCORD ROLE ID<input value={grant.roleId} onChange={e=>updateGameRole(index,{roleId:e.target.value.trim()})} placeholder="Discord role snowflake"/></label><label>SERVER<select value={grant.serverId} onChange={e=>updateGameRole(index,{serverId:e.target.value})}>{servers.map(server=><option value={server.id} key={server.id}>{server.name}</option>)}</select></label><label>RANK NAME<input value={grant.groupName} onChange={e=>updateGameRole(index,{groupName:e.target.value.trim()})} placeholder="moderator"/></label><label>PRIORITY<input type="number" value={grant.priority} onChange={e=>updateGameRole(index,{priority:Number(e.target.value)})}/></label><button type="button" className="danger" onClick={()=>setSettings({...settings,discordGameRoleGrants:(settings.discordGameRoleGrants ?? []).filter((_,i)=>i!==index)})}>REMOVE</button></div>
        <div className="form-row"><label>BADGE TEXT<input value={grant.badgeText??''} onChange={e=>updateGameRole(index,{badgeText:e.target.value})} placeholder="MODERATOR"/></label><label>BADGE COLOR<input value={grant.badgeColor??'silver'} onChange={e=>updateGameRole(index,{badgeColor:e.target.value.trim()})} placeholder="silver"/></label><label>KICK POWER<input type="number" min={0} max={255} value={grant.kickPower??0} onChange={e=>updateGameRole(index,{kickPower:Number(e.target.value)})}/></label><label>REQUIRED KICK POWER<input type="number" min={0} max={255} value={grant.requiredKickPower??0} onChange={e=>updateGameRole(index,{requiredKickPower:Number(e.target.value)})}/></label></div>
        <div className="permission-chip-grid game-role-flags"><label className="check-row"><input type="checkbox" checked={grant.hidden??false} onChange={e=>updateGameRole(index,{hidden:e.target.checked})}/> Hidden badge</label><label className="check-row"><input type="checkbox" checked={grant.cover??true} onChange={e=>updateGameRole(index,{cover:e.target.checked})}/> Cover global badge</label><label className="check-row"><input type="checkbox" checked={grant.reservedSlot??false} onChange={e=>updateGameRole(index,{reservedSlot:e.target.checked})}/> Reserved slot</label></div>
        <label>INHERITED RANK NAMES<input value={(grant.inheritedGroups??[]).join(', ')} onChange={e=>updateGameRole(index,{inheritedGroups:e.target.value.split(',').map(x=>x.trim()).filter(Boolean)})} placeholder="helper, junior-moderator"/></label>
        <div className="game-permission-selector"><span className="eyebrow">REMOTE ADMIN PERMISSIONS</span><div className="permission-chip-grid">{gameRaPermissions.map(permission=>{const selected=grant.permissions??[];return <label className="check-row" key={permission}><input type="checkbox" checked={selected.includes(permission)} onChange={()=>updateGameRole(index,{permissions:selected.includes(permission)?selected.filter(x=>x!==permission):[...selected,permission]})}/>{permission}</label>})}</div></div>
        <label>PLUGIN PERMISSIONS<input value={(grant.pluginPermissions??[]).join(', ')} onChange={e=>updateGameRole(index,{pluginPermissions:e.target.value.split(',').map(x=>x.trim()).filter(Boolean)})} placeholder="plugin.permission, another.permission"/></label>
      </section>)}
      {!(settings.discordGameRoleGrants?.length)&&<p className="muted">No in-game role mappings configured.</p>}
    </div>
    <p>Commands: <code>/scp status</code>, <code>players</code>, <code>player</code>, <code>kick</code>, <code>mute</code>, <code>ban</code>, <code>start</code>, <code>stop</code>, <code>restart</code>, and <code>announce</code>. Stop, restart, and moderation commands require explicit confirmation.</p>
    <div className="button-row form-actions"><button className="primary"><Save size={14}/> SAVE CHANGES</button><button type="button" onClick={async()=>{try{await api('/integrations/discord/bot/reconnect',{method:'POST'});setTimeout(()=>void loadStatus(),1200)}catch(error){onError(error instanceof Error ? error.message : 'Unable to reconnect bot')}}}><RefreshCw size={14}/> RECONNECT BOT</button></div>
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
