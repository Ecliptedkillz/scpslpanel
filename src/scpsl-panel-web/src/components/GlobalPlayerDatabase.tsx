import { useEffect, useMemo, useState } from 'react'
import { ExternalLink, Search, UserRound, X } from 'lucide-react'
import { api } from '../api'
import type { StoredPlayer } from './PlayerHistoryView'

type GlobalPlayer = { serverId: string; serverName: string; player: StoredPlayer }
const playtime = (seconds: number) => seconds >= 3600
  ? `${Math.floor(seconds / 3600)}h ${Math.floor(seconds % 3600 / 60)}m`
  : `${Math.floor(seconds / 60)}m`

export function GlobalPlayerDatabase({ onError }: { onError: (message: string) => void }) {
  const [records,setRecords] = useState<GlobalPlayer[]>([])
  const [search,setSearch] = useState('')
  const [selected,setSelected] = useState<GlobalPlayer | null>(null)
  useEffect(() => { api<GlobalPlayer[]>('/players/global').then(setRecords).catch(e=>onError(e.message)) }, [onError])
  const filtered = useMemo(() => {
    const query=search.trim().toLowerCase()
    if (!query) return records
    return records.filter(({serverName,player}) => [
      serverName,player.currentName,player.userId,player.discordId,player.steamDisplayName,
      player.discordDisplayName,...(player.discordRoles ?? []),...player.nameHistory.map(x=>x.name),
    ].some(value=>value?.toLowerCase().includes(query)))
  },[records,search])
  return <>
    <div className="global-player-toolbar"><div><span className="eyebrow">IDENTITY INTELLIGENCE</span><h1>Player Database</h1><p>Search linked player identities across every server you can access.</p></div><label><Search size={18}/><input value={search} onChange={e=>setSearch(e.target.value)} placeholder="Search name, Steam, Discord, role…"/></label></div>
    <div className="player-db-summary"><div><strong>{records.length}</strong><span>KNOWN PLAYERS</span></div><div><strong>{records.filter(x=>x.player.discordId).length}</strong><span>DISCORD LINKS</span></div><div><strong>{new Set(records.map(x=>x.serverId)).size}</strong><span>SERVERS</span></div></div>
    <div className="global-player-grid">{filtered.map(record => {
      const player=record.player
      return <button className="global-player-card" key={`${record.serverId}:${player.id}`} onClick={()=>setSelected(record)}>
        <div className="dual-avatar">{player.steamAvatarUrl ? <img src={player.steamAvatarUrl} alt=""/> : <UserRound/>}{player.discordAvatarUrl && <img src={player.discordAvatarUrl} alt=""/>}</div>
        <div><span className="eyebrow">{record.serverName}</span><h3>{player.currentName}</h3><p>{player.steamDisplayName ?? player.userId}</p><p>{player.discordDisplayName ?? (player.discordId ? `Discord ${player.discordId}` : 'Discord not linked')}</p></div>
        <span className="tag">{playtime(player.playtimeSeconds)}</span>
      </button>
    })}</div>
    {!filtered.length && <div className="empty-mini">No matching players found.</div>}
    {selected && <GlobalProfileModal record={selected} close={()=>setSelected(null)}/>}
  </>
}

function GlobalProfileModal({record,close}:{record:GlobalPlayer;close:()=>void}) {
  const player=record.player
  return <div className="modal-backdrop"><article className="modal global-profile-modal">
    <header className="global-profile-hero">
      <div className="dual-avatar large">{player.steamAvatarUrl ? <img src={player.steamAvatarUrl} alt="Steam avatar"/> : <UserRound/>}{player.discordAvatarUrl && <img src={player.discordAvatarUrl} alt="Discord avatar"/>}</div>
      <div><span className="eyebrow">{record.serverName} · PLAYER PROFILE</span><h2>{player.currentName}</h2><p>{player.steamDisplayName ?? player.userId} {player.discordDisplayName ? `· ${player.discordDisplayName}` : ''}</p></div>
      <button className="icon-button" onClick={close}><X/></button>
    </header>
    <div className="identity-profile-grid">
      <section><span className="eyebrow">STEAM IDENTITY</span><strong>{player.steamDisplayName ?? player.currentName}</strong><code>{player.userId}</code>{player.steamProfileUrl && <a href={player.steamProfileUrl} target="_blank" rel="noreferrer">OPEN STEAM PROFILE <ExternalLink size={13}/></a>}</section>
      <section><span className="eyebrow">DISCORD IDENTITY</span><strong>{player.discordDisplayName ?? 'Not linked'}</strong><code>{player.discordId ?? 'No Discord ID'}</code><div>{player.discordRoles?.map(role=><span className="tag" key={role}>{role}</span>)}</div></section>
    </div>
    <div className="profile-stats"><div><span>FIRST SEEN</span><strong>{new Date(player.firstConnectedAt).toLocaleDateString()}</strong></div><div><span>LAST SEEN</span><strong>{new Date(player.lastConnectedAt).toLocaleString()}</strong></div><div><span>PLAYTIME</span><strong>{playtime(player.playtimeSeconds)}</strong></div><div><span>CONNECTIONS</span><strong>{player.connections}</strong></div></div>
    <div className="global-profile-columns">
      <section><h3>KNOWN NAMES</h3>{player.nameHistory.slice().reverse().map(name=><div className="history-entry" key={name.name}><strong>{name.name}</strong><small>{new Date(name.lastSeenAt).toLocaleString()}</small></div>)}</section>
      <section><h3>MODERATION HISTORY</h3>{player.moderationHistory.slice().reverse().map(item=><div className="history-entry" key={item.id}><strong><span className="tag red">{item.type.toUpperCase()}</span> {item.reason}</strong><small>{item.actor} · {new Date(item.at).toLocaleString()}</small></div>)}{!player.moderationHistory.length && <div className="empty-mini">No moderation history.</div>}</section>
      <section><h3>STAFF NOTES</h3>{player.notes.slice().reverse().map(note=><div className="history-entry" key={note.id}><strong>{note.text}</strong><small>{note.actor} · {new Date(note.at).toLocaleString()}</small></div>)}{!player.notes.length && <div className="empty-mini">No staff notes.</div>}</section>
    </div>
  </article></div>
}
