import { useEffect, useMemo, useState } from 'react'
import { AlertTriangle, Ban, Clock3, ExternalLink, FileText, History, IdCard,
  Link2, RotateCcw, Search, Shield, StickyNote, Tags, UserCheck, UserRound, Users, X } from 'lucide-react'
import { api } from '../api'
import type { StoredPlayer } from './PlayerHistoryView'

type GlobalPlayer = { serverId: string; serverName: string; player: StoredPlayer }
type LinkHealth = {
  serverId: string; serverName: string; line: number; steamId: string; discordId: string
  valid: boolean; issue?: string
}
type ModerationRow = { record: GlobalPlayer; item: StoredPlayer['moderationHistory'][number] }
const playtime = (seconds: number) => seconds >= 3600
  ? `${Math.floor(seconds / 3600)}h ${Math.floor(seconds % 3600 / 60)}m`
  : `${Math.floor(seconds / 60)}m`

export function GlobalPlayerDatabase({ onError }: { onError: (message: string) => void }) {
  const [records,setRecords] = useState<GlobalPlayer[]>([])
  const [search,setSearch] = useState('')
  const [selected,setSelected] = useState<GlobalPlayer | null>(null)
  const [health,setHealth] = useState<LinkHealth[]>([])
  const [tab,setTab] = useState<'players'|'watchlist'|'moderation'|'links'>('players')
  const [moderationSearch,setModerationSearch] = useState('')
  const [moderationType,setModerationType] = useState('all')
  const [moderationActor,setModerationActor] = useState('all')
  const [selectedModeration,setSelectedModeration] = useState<ModerationRow|null>(null)
  useEffect(() => {
    Promise.all([
      api<GlobalPlayer[]>('/players/global').then(setRecords),
      api<LinkHealth[]>('/players/identity-health').then(setHealth),
    ]).catch(e=>onError(e.message))
  }, [onError])
  const issues=health.filter(x=>!x.valid)
  const displayedRecords=tab==='watchlist'
    ? records.filter(x=>x.player.moderationHistory.some(item=>item.type==='watchlist'))
    : records
  const filtered = useMemo(() => {
    const query=search.trim().toLowerCase()
    if (!query) return displayedRecords
    return displayedRecords.filter(({serverName,player}) => [
      serverName,player.currentName,player.userId,player.discordId,player.steamDisplayName,
      player.discordDisplayName,...(player.discordRoles ?? []),...player.nameHistory.map(x=>x.name),
    ].some(value=>value?.toLowerCase().includes(query)))
  },[displayedRecords,search])
  const moderationRows=useMemo(()=>records.flatMap(record=>record.player.moderationHistory
    .map(item=>({record,item}))).sort((a,b)=>b.item.at.localeCompare(a.item.at)),[records])
  const moderationActors=[...new Set(moderationRows.map(row=>row.item.actor))].sort()
  const visibleModeration=moderationRows.filter(({record,item})=>{
    const query=moderationSearch.trim().toLowerCase()
    return (moderationType==='all'||item.type===moderationType)
      &&(moderationActor==='all'||item.actor===moderationActor)
      &&(!query||[record.player.currentName,record.player.userId,item.reason,item.actor,item.id]
        .some(value=>value.toLowerCase().includes(query)))
  })
  const sevenDaysAgo=Date.now()-7*86400000
  const warnings=moderationRows.filter(row=>row.item.type==='warning')
  const bans=moderationRows.filter(row=>row.item.type==='ban')
  const staffActivity=moderationActors.map(actor=>{
    const actions=moderationRows.filter(row=>row.item.actor===actor)
    return {actor,total:actions.length,recent:actions.filter(row=>new Date(row.item.at).getTime()>=sevenDaysAgo).length,
      warnings:actions.filter(row=>row.item.type==='warning').length,bans:actions.filter(row=>row.item.type==='ban').length}
  }).sort((a,b)=>b.total-a.total).slice(0,4)
  return <>
    <div className="global-player-toolbar"><div><span className="eyebrow">IDENTITY INTELLIGENCE</span><h1>Player Database</h1><p>Search linked player identities across every server you can access.</p></div><label><Search size={18}/><input value={search} onChange={e=>setSearch(e.target.value)} placeholder="Search name, Steam, Discord, role…"/></label></div>
    <nav className="page-tabs database-tabs"><button className={tab==='players'?'active':''} onClick={()=>setTab('players')}>All players <span>{records.length}</span></button><button className={tab==='watchlist'?'active':''} onClick={()=>setTab('watchlist')}>Watchlist <span>{records.filter(x=>x.player.moderationHistory.some(item=>item.type==='watchlist')).length}</span></button><button className={tab==='moderation'?'active':''} onClick={()=>setTab('moderation')}>Moderation center <span>{records.reduce((n,x)=>n+x.player.moderationHistory.length,0)}</span></button><button className={tab==='links'?'active':''} onClick={()=>setTab('links')}>Identity links <span className={issues.length?'bad-count':''}>{issues.length||'OK'}</span></button></nav>
    <div className="player-db-summary"><div><strong>{records.length}</strong><span>KNOWN PLAYERS</span></div><div><strong>{records.filter(x=>x.player.discordId).length}</strong><span>DISCORD LINKS</span></div><div><strong>{new Set(records.map(x=>x.serverId)).size}</strong><span>SERVERS</span></div><div><strong>{records.filter(x=>x.player.moderationHistory.length).length}</strong><span>MODERATED</span></div></div>
    {tab==='links' && <section className="identity-health-panel">
      <header><div><span className="eyebrow">DISCORD LINKS.CSV</span><h3>Identity-link health</h3></div><span className={`tag ${issues.length ? 'red' : ''}`}>{issues.length ? `${issues.length} ISSUE${issues.length === 1 ? '' : 'S'}` : 'HEALTHY'}</span></header>
      {!issues.length && <p>All {health.length} configured links use valid, unique Steam and Discord IDs.</p>}
      {issues.map(item=><div className="identity-health-row" key={`${item.serverId}:${item.line}`}>
        <span>{item.serverName}</span><code>LINE {item.line}</code><code>{item.steamId || 'missing Steam ID'}</code><code>{item.discordId || 'missing Discord ID'}</code><strong>{item.issue}</strong>
      </div>)}
    </section>}
    {tab==='moderation'&&<section className="moderation-dashboard">
      <div className="moderation-kpis"><div><span>Total warnings</span><AlertTriangle/><strong>{warnings.length}</strong></div><div><span>New warnings last 7d</span><Clock3/><strong>+{warnings.filter(row=>new Date(row.item.at).getTime()>=sevenDaysAgo).length}</strong></div><div><span>Total bans</span><Ban/><strong>{bans.length}</strong></div><div><span>New bans last 7d</span><Clock3/><strong>+{bans.filter(row=>new Date(row.item.at).getTime()>=sevenDaysAgo).length}</strong></div></div>
      <section className="staff-activity-panel"><header><div><Shield size={16}/><strong>Staff activity</strong></div><span>{moderationActors.length} moderators</span></header><div className="staff-activity-grid">{staffActivity.map(staff=><article key={staff.actor}><div><strong>{staff.actor}</strong><b>{staff.total}</b></div><small>{staff.recent} in last 7d</small><i><span style={{width:`${Math.min(100,staff.total/Math.max(1,staffActivity[0]?.total)*100)}%`}}/></i><footer><span>{staff.warnings} warns</span><span>{staff.bans} bans</span></footer></article>)}</div></section>
      <div className="moderation-filters"><label><Search size={17}/><input value={moderationSearch} onChange={e=>setModerationSearch(e.target.value)} placeholder="Search player, reason, action ID…"/></label><select value={moderationType} onChange={e=>setModerationType(e.target.value)}><option value="all">Any action</option>{[...new Set(moderationRows.map(row=>row.item.type))].map(type=><option key={type}>{type}</option>)}</select><select value={moderationActor} onChange={e=>setModerationActor(e.target.value)}><option value="all">Any administrator</option>{moderationActors.map(actor=><option key={actor}>{actor}</option>)}</select></div>
      <div className="moderation-table-wrap"><table><thead><tr><th>ACTION</th><th>PLAYER</th><th>REASON</th><th>SERVER</th><th>AUTHOR</th><th>DATE / TIME</th></tr></thead><tbody>{visibleModeration.map(row=><tr key={row.item.id} onClick={()=>setSelectedModeration(row)}><td><span className={`action-kind ${row.item.type}`}>{row.item.type==='ban'?<Ban size={14}/>:<AlertTriangle size={14}/>}<b>{row.item.type.toUpperCase()}</b></span><small>{row.item.id.slice(0,8).toUpperCase()}</small></td><td><strong>{row.record.player.currentName}</strong><small>{row.record.player.userId}</small></td><td className="moderation-reason">{row.item.reason}</td><td>{row.record.serverName}</td><td>{row.item.actor}</td><td>{new Date(row.item.at).toLocaleString()}</td></tr>)}</tbody></table>{!visibleModeration.length&&<div className="empty-mini">No moderation actions match these filters.</div>}</div>
    </section>}
    {tab!=='links'&&tab!=='moderation'&&<div className="global-player-grid">{filtered.map(record => {
      const player=record.player
      return <button className="global-player-card" key={`${record.serverId}:${player.id}`} onClick={()=>setSelected(record)}>
        <div className="dual-avatar">{player.steamAvatarUrl ? <img src={player.steamAvatarUrl} alt=""/> : <UserRound/>}{player.discordAvatarUrl && <img src={player.discordAvatarUrl} alt=""/>}</div>
        <div><span className="eyebrow">{record.serverName}</span><h3>{player.currentName}</h3><p>{player.steamDisplayName ?? player.userId}</p><p>{player.discordDisplayName ?? (player.discordId ? `Discord ${player.discordId}` : 'Discord not linked')}</p></div>
        <span className="tag">{playtime(player.playtimeSeconds)}</span>
      </button>
    })}</div>}
    {tab!=='links'&&tab!=='moderation'&&!filtered.length && <div className="empty-mini">No matching players found.</div>}
    {selected && <GlobalProfileModal record={selected} close={()=>setSelected(null)} onError={onError} onUpdated={updated=>{const player={...selected.player,...updated};setSelected({...selected,player});setRecords(records.map(x=>x.serverId===selected.serverId&&x.player.id===player.id?{...x,player}:x))}}/>}
    {selectedModeration&&<div className="modal-backdrop"><article className="modal moderation-detail-modal"><header><div><span className="action-id">[{selectedModeration.item.id.slice(0,8).toUpperCase()}]</span><h2>{selectedModeration.item.type.toUpperCase()} · {selectedModeration.record.player.currentName}</h2></div><button className="icon-button" onClick={()=>setSelectedModeration(null)}><X/></button></header><div className="moderation-detail-layout"><nav><button className="active"><FileText/>Info</button><button onClick={()=>{setSelected(selectedModeration.record);setSelectedModeration(null)}}><UserRound/>Player</button></nav><section><dl><dt>Date/time</dt><dd>{new Date(selectedModeration.item.at).toLocaleString()}</dd><dt>Administrator</dt><dd>{selectedModeration.item.actor}</dd><dt>Player</dt><dd>{selectedModeration.record.player.currentName}</dd><dt>Server</dt><dd>{selectedModeration.record.serverName}</dd></dl><h4>Reason</h4><p>{selectedModeration.item.reason}</p></section></div></article></div>}
  </>
}

function GlobalProfileModal({record,close,onError,onUpdated}:{record:GlobalPlayer;close:()=>void;onError:(message:string)=>void;onUpdated:(player:StoredPlayer)=>void}) {
  const player=record.player
  const [note,setNote]=useState('')
  const [discordId,setDiscordId]=useState(player.discordId ?? '')
  const [pendingAction,setPendingAction]=useState<string|null>(null)
  const [actionReason,setActionReason]=useState('')
  const [templates,setTemplates]=useState<string[]>(()=>{
    try{return JSON.parse(localStorage.getItem('scpcontrol-moderation-templates')??'[]') as string[]}catch{return []}
  })
  const [profileTab,setProfileTab]=useState<'info'|'history'|'notes'|'names'|'ids'>('info')
  const [selectedProfileAction,setSelectedProfileAction]=useState<StoredPlayer['moderationHistory'][number]|null>(null)
  const risk=Math.min(100,player.moderationHistory.reduce((score,item)=>score+(item.type==='ban'?35:item.type==='kick'?20:item.type==='warning'?10:item.type==='watchlist'?25:5),0)+Math.max(0,player.nameHistory.length-2)*3)
  const openAction=(type:string)=>{setPendingAction(type);setActionReason(`${type} added from global profile`)}
  const saveTemplate=()=>{
    const value=actionReason.trim()
    if(!value||templates.includes(value))return
    const next=[...templates,value].slice(-12);setTemplates(next);localStorage.setItem('scpcontrol-moderation-templates',JSON.stringify(next))
  }
  const removeTemplate=(value:string)=>{
    const next=templates.filter(item=>item!==value);setTemplates(next);localStorage.setItem('scpcontrol-moderation-templates',JSON.stringify(next))
  }
  const recordAction=async()=>{
    if(!pendingAction||!actionReason.trim())return
    try{onUpdated(await api<StoredPlayer>(`/servers/${record.serverId}/player-history/${player.id}/actions`,{method:'POST',body:JSON.stringify({type:pendingAction,reason:actionReason,durationMinutes:null})}));setPendingAction(null)}catch(error){onError(error instanceof Error?error.message:'Unable to record action')}
  }
  const addNote=async()=>{
    if(!note.trim())return
    try{onUpdated(await api<StoredPlayer>(`/servers/${record.serverId}/player-history/${player.id}/notes`,{method:'POST',body:JSON.stringify({text:note})}));setNote('')}catch(error){onError(error instanceof Error?error.message:'Unable to add note')}
  }
  const saveLink=async()=>{
    try{await api(`/servers/${record.serverId}/players/identity-link`,{method:'PUT',body:JSON.stringify({steamId:player.userId.split('@')[0],discordId})});onUpdated({...player,discordId})}catch(error){onError(error instanceof Error?error.message:'Unable to save identity link')}
  }
  const revokeBan=async()=>{
    if(!selectedProfileAction)return
    try{
      const updated=await api<StoredPlayer>(`/servers/${record.serverId}/player-history/${player.id}/actions/${selectedProfileAction.id}/revoke`,{method:'POST'})
      onUpdated(updated);setSelectedProfileAction(null)
    }catch(error){onError(error instanceof Error?error.message:'Unable to revoke ban')}
  }
  return <div className="modal-backdrop"><article className="modal global-profile-modal">
    <header className="global-profile-hero">
      <div className="dual-avatar large">{player.steamAvatarUrl ? <img src={player.steamAvatarUrl} alt="Steam avatar"/> : <UserRound/>}{player.discordAvatarUrl && <img src={player.discordAvatarUrl} alt="Discord avatar"/>}</div>
      <div><span className="eyebrow">{record.serverName} · PLAYER PROFILE</span><h2>{player.currentName}</h2><p>{player.steamDisplayName ?? player.userId} {player.discordDisplayName ? `· ${player.discordDisplayName}` : ''}</p></div>
      <button className="icon-button" onClick={close}><X/></button>
    </header>
    <div className="global-profile-shell">
      <nav className="global-profile-nav">
        <button className={profileTab==='info'?'active':''} onClick={()=>setProfileTab('info')}><IdCard/>Info</button>
        <button className={profileTab==='history'?'active':''} onClick={()=>setProfileTab('history')}><History/>History <span>{player.moderationHistory.length}</span></button>
        <button className={profileTab==='notes'?'active':''} onClick={()=>setProfileTab('notes')}><StickyNote/>Notes <span>{player.notes.length}</span></button>
        <button className={profileTab==='names'?'active':''} onClick={()=>setProfileTab('names')}><Users/>Names <span>{player.nameHistory.length}</span></button>
        <button className={profileTab==='ids'?'active':''} onClick={()=>setProfileTab('ids')}><Tags/>IDs</button>
      </nav>
      <main className={`global-profile-content profile-tab-${profileTab}`}>
    <div className="identity-profile-grid">
      <section><span className="eyebrow">STEAM IDENTITY</span><strong>{player.steamDisplayName ?? player.currentName}</strong><code>{player.userId}</code>{player.steamProfileUrl && <a href={player.steamProfileUrl} target="_blank" rel="noreferrer">OPEN STEAM PROFILE <ExternalLink size={13}/></a>}</section>
      <section><span className="eyebrow">DISCORD IDENTITY</span><strong>{player.discordDisplayName ?? (player.discordId ? 'Linked · profile unavailable' : 'Not linked')}</strong><code>{player.discordId ?? 'No Discord ID'}</code><div>{player.discordRoles?.map(role=><span className="tag" key={role}>{role}</span>)}</div>{player.discordId && !player.discordRoles?.length && <small className="identity-hint">Enable Guild Members intent to load server nickname and roles.</small>}</section>
    </div>
    <div className="profile-stats"><div><span>FIRST SEEN</span><strong>{new Date(player.firstConnectedAt).toLocaleDateString()}</strong></div><div><span>LAST SEEN</span><strong>{new Date(player.lastConnectedAt).toLocaleString()}</strong></div><div><span>PLAYTIME</span><strong>{playtime(player.playtimeSeconds)}</strong></div><div><span>RISK SCORE</span><strong className={risk>=40?'risk-high':''}>{risk}/100</strong></div></div>
    <div className="global-profile-actions">
      <div className="profile-moderation-buttons">
        <button className="warning-action" onClick={()=>openAction('warning')}><AlertTriangle/>Add warning</button>
        <button onClick={()=>openAction('watchlist')}><Shield/>Watchlist</button>
        <button onClick={()=>openAction('allowlist')}><UserCheck/>Allowlist</button>
      </div>
      <div className="profile-utility-actions">
        <label><span>DISCORD ID</span><div><input value={discordId} onChange={e=>setDiscordId(e.target.value.trim())} placeholder="Discord user ID"/><button title="Save Discord link" onClick={saveLink}><Link2/>Save</button></div></label>
        <label className="profile-note-control"><span>STAFF NOTE</span><div><input value={note} onChange={e=>setNote(e.target.value)} placeholder="Write a private staff note…"/><button className="primary" onClick={addNote}><StickyNote/>Add note</button></div></label>
      </div>
    </div>
    <div className="global-profile-columns">
      <section><h3>KNOWN NAMES</h3>{player.nameHistory.slice().reverse().map(name=><div className="history-entry" key={name.name}><strong>{name.name}</strong><small>{new Date(name.lastSeenAt).toLocaleString()}</small></div>)}</section>
      <section><h3>MODERATION HISTORY</h3>{player.moderationHistory.slice().reverse().map(item=><button className="history-entry history-action-row" key={item.id} onClick={()=>setSelectedProfileAction(item)}><strong><span className={`tag ${item.revoked?'':'red'}`}>{item.type.toUpperCase()}</span> {item.reason}</strong><small>{item.actor} · {new Date(item.at).toLocaleString()} {item.revoked?'· REVOKED':''}</small></button>)}{!player.moderationHistory.length && <div className="empty-mini">No moderation history.</div>}</section>
      <section><h3>STAFF NOTES</h3>{player.notes.slice().reverse().map(note=><div className="history-entry" key={note.id}><strong>{note.text}</strong><small>{note.actor} · {new Date(note.at).toLocaleString()}</small></div>)}{!player.notes.length && <div className="empty-mini">No staff notes.</div>}</section>
    </div>
      </main>
    </div>
    {pendingAction&&<div className="modal-backdrop nested-modal"><form className="modal action-dialog" onSubmit={e=>{e.preventDefault();void recordAction()}}><header><div><span className="eyebrow">PLAYER ACTION</span><h2>{pendingAction.toUpperCase()}</h2><p>Record this action for <strong>{player.currentName}</strong>.</p></div><button type="button" className="icon-button" onClick={()=>setPendingAction(null)}><X/></button></header>{templates.length>0&&<div className="moderation-templates"><span>SAVED RESPONSES</span>{templates.map(template=><button type="button" key={template} title="Right-click to remove" onClick={()=>setActionReason(template)} onContextMenu={event=>{event.preventDefault();removeTemplate(template)}}>{template}</button>)}</div>}<label>REASON<textarea autoFocus required value={actionReason} onChange={e=>setActionReason(e.target.value)} /></label><div className="template-save-row"><button type="button" onClick={saveTemplate}><StickyNote size={14}/> SAVE AS RESPONSE</button><small>Right-click a saved response to remove it.</small></div><footer><button type="button" onClick={()=>setPendingAction(null)}>CANCEL</button><button className="primary">CONFIRM {pendingAction.toUpperCase()}</button></footer></form></div>}
    {selectedProfileAction&&<div className="modal-backdrop nested-modal"><article className="modal player-action-detail"><header><div><span className="action-id">[{selectedProfileAction.id.slice(0,8).toUpperCase()}]</span><h2>{selectedProfileAction.type.toUpperCase()} · {player.currentName}</h2></div><button className="icon-button" onClick={()=>setSelectedProfileAction(null)}><X/></button></header><div className="player-action-detail-body"><dl><dt>Date/time</dt><dd>{new Date(selectedProfileAction.at).toLocaleString()}</dd><dt>Administrator</dt><dd>{selectedProfileAction.actor}</dd><dt>Duration</dt><dd>{selectedProfileAction.durationMinutes?`${selectedProfileAction.durationMinutes} minutes`:'Not specified'}</dd><dt>Status</dt><dd>{selectedProfileAction.revoked?'Revoked':'Active'}</dd></dl><h4>Reason</h4><p>{selectedProfileAction.reason}</p></div><footer><button onClick={()=>setSelectedProfileAction(null)}>CLOSE</button>{['ban','oban'].includes(selectedProfileAction.type)&&!selectedProfileAction.revoked&&<button className="danger solid" onClick={()=>void revokeBan()}><RotateCcw/>REVOKE BAN</button>}</footer></article></div>}
  </article></div>
}
