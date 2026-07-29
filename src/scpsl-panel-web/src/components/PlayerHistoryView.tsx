import { FormEvent } from 'react'
import { X } from 'lucide-react'
import { api } from '../api'
import type { Server } from '../types'

export type StoredPlayer = {
  id: string; userId: string; discordId?: string | null; lastIpAddress: string; currentName: string
  steamDisplayName?: string | null; steamAvatarUrl?: string | null; steamProfileUrl?: string | null
  discordDisplayName?: string | null; discordAvatarUrl?: string | null; discordRoles?: string[] | null
  firstConnectedAt: string; lastConnectedAt: string; playtimeSeconds: number; connections: number
  nameHistory: { name: string; firstSeenAt: string; lastSeenAt: string }[]
  moderationHistory: { id: string; type: string; reason: string; actor: string; at: string; durationMinutes: number | null; revoked?: boolean }[]
  notes: { id: string; text: string; actor: string; at: string }[]
}

const playtime = (seconds: number) => seconds >= 3600
  ? `${Math.floor(seconds / 3600)}h ${Math.floor(seconds % 3600 / 60)}m`
  : `${Math.floor(seconds / 60)}m`
const ago = (value: string) => {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000))
  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  return `${Math.floor(seconds / 86400)}d ago`
}

export function PlayerHistoryView({
  server, history, profile, setProfile, note, setNote, reload, onError,
}: {
  server: Server; history: StoredPlayer[]; profile: StoredPlayer | null
  setProfile: (value: StoredPlayer | null) => void; note: string; setNote: (value: string) => void
  reload: () => void; onError: (value: string) => void
}) {
  const recordAction = async (type: string) => {
    if (!profile) return
    const reason = prompt(`Reason for ${type}:`, type === 'warning' ? 'Staff warning' : `${type} status changed`)
    if (reason === null) return
    try {
      const updated = await api<StoredPlayer>(`/servers/${server.id}/player-history/${profile.id}/actions`,
        { method:'POST', body:JSON.stringify({type,reason,durationMinutes:null}) })
      setProfile({...updated, discordId:updated.discordId ?? profile.discordId}); reload()
    } catch (error) { onError(error instanceof Error ? error.message : 'Unable to record action') }
  }
  const addNote = async (event: FormEvent) => {
    event.preventDefault()
    if (!profile || !note.trim()) return
    try {
      const updated = await api<StoredPlayer>(`/servers/${server.id}/player-history/${profile.id}/notes`,
        { method:'POST', body:JSON.stringify({text:note}) })
      setProfile({...updated, discordId:updated.discordId ?? profile.discordId}); setNote(''); reload()
    } catch (error) { onError(error instanceof Error ? error.message : 'Unable to add note') }
  }
  const copyIdentifiers = async () => {
    if (!profile) return
    await navigator.clipboard.writeText([
      `Steam: ${profile.userId}`, profile.discordId ? `Discord: ${profile.discordId}` : '',
      `IP: ${profile.lastIpAddress}`,
    ].filter(Boolean).join('\n'))
  }

  return <section className="player-database">
    <div className="player-db-summary"><div><strong>{history.length}</strong><span>KNOWN PLAYERS</span></div><div><strong>{history.reduce((sum,x) => sum + x.connections, 0)}</strong><span>CONNECTIONS</span></div><div><strong>{history.filter(x => x.discordId).length}</strong><span>DISCORD LINKS</span></div></div>
    <div className="table-wrap"><table><thead><tr><th>PLAYER</th><th>STEAM</th><th>DISCORD</th><th>ROLES</th><th>LAST CONNECTED</th><th>PLAYTIME</th><th/></tr></thead><tbody>{history.map(player => <tr key={player.id}><td><strong>{player.currentName}</strong><small>{player.nameHistory.length} known name{player.nameHistory.length === 1 ? '' : 's'}</small></td><td><div className="identity-cell">{player.steamAvatarUrl && <img src={player.steamAvatarUrl} alt=""/>}<span><strong>{player.steamDisplayName ?? 'Steam'}</strong><small className="mono">{player.userId}</small></span></div></td><td><div className="identity-cell">{player.discordAvatarUrl && <img src={player.discordAvatarUrl} alt=""/>}<span><strong>{player.discordDisplayName ?? (player.discordId ? 'Discord' : 'Not linked')}</strong><small className="mono">{player.discordId ?? '—'}</small></span></div></td><td>{player.discordRoles?.length ? player.discordRoles.slice(0,3).map(role=><span className="tag" key={role}>{role}</span>) : '—'}</td><td>{ago(player.lastConnectedAt)}</td><td>{playtime(player.playtimeSeconds)}</td><td><button className="manage-button" onClick={() => setProfile(player)}>VIEW PROFILE</button></td></tr>)}</tbody></table></div>
    {!history.length && <div className="empty-mini">Records will appear after LabAPI bridge heartbeats are received.</div>}
    {profile && <div className="modal-backdrop"><div className="modal player-profile">
      <div className="modal-head"><div className="profile-identities">{profile.steamAvatarUrl && <img src={profile.steamAvatarUrl} alt="Steam avatar"/>}{profile.discordAvatarUrl && <img src={profile.discordAvatarUrl} alt="Discord avatar"/>}<div><span className="eyebrow">PLAYER PROFILE</span><h2>{profile.currentName}</h2><p className="mono">Steam: {profile.steamDisplayName ?? profile.userId}</p><p className="mono">Discord: {profile.discordDisplayName ?? profile.discordId ?? 'Not linked'}</p>{profile.discordRoles?.length ? <p>{profile.discordRoles.map(role=><span className="tag" key={role}>{role}</span>)}</p> : null}</div></div><button className="icon-button" onClick={() => setProfile(null)}><X/></button></div>
      <div className="profile-action-bar">{profile.steamProfileUrl && <a href={profile.steamProfileUrl} target="_blank" rel="noreferrer">OPEN STEAM PROFILE</a>}<button onClick={() => recordAction('warning')}>ADD WARNING</button><button onClick={() => recordAction('watchlist')}>WATCHLIST</button><button onClick={() => recordAction('allowlist')}>ALLOWLIST</button><button onClick={() => void copyIdentifiers()}>COPY IDENTIFIERS</button></div>
      <div className="profile-stats"><div><span>FIRST CONNECTED</span><strong>{new Date(profile.firstConnectedAt).toLocaleString()}</strong></div><div><span>LAST CONNECTED</span><strong>{new Date(profile.lastConnectedAt).toLocaleString()}</strong></div><div><span>PLAYTIME</span><strong>{playtime(profile.playtimeSeconds)}</strong></div><div><span>CONNECTIONS</span><strong>{profile.connections}</strong></div></div>
      <div className="profile-columns"><section><h3>NAME HISTORY</h3>{profile.nameHistory.map(item => <div className="history-entry" key={item.name}><strong>{item.name}</strong><small>Last used {new Date(item.lastSeenAt).toLocaleString()}</small></div>)}</section><section><h3>MODERATION HISTORY</h3>{profile.moderationHistory.slice().reverse().map(item => <div className="history-entry" key={item.id}><strong><span className="tag red">{item.type.toUpperCase()}</span> {item.reason}</strong><small>{item.actor} · {new Date(item.at).toLocaleString()}</small></div>)}{!profile.moderationHistory.length && <div className="empty-mini">No moderation history.</div>}</section></div>
      <section className="profile-notes"><h3>STAFF NOTES</h3>{profile.notes.slice().reverse().map(item => <div className="history-entry" key={item.id}><strong>{item.text}</strong><small>{item.actor} · {new Date(item.at).toLocaleString()}</small></div>)}<form onSubmit={addNote}><input value={note} onChange={event => setNote(event.target.value)} placeholder="Add a private staff note…"/><button className="primary">ADD NOTE</button></form></section>
    </div></div>}
  </section>
}
