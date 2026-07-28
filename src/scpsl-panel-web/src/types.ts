export type ServerState = 'offline' | 'starting' | 'online' | 'stopping' | 'faulted'

export interface Server {
  id: string
  name: string
  state: ServerState
  processId: number | null
  startedAt: string | null
  memoryBytes: number
  cpuPercent: number
  players: number
  maxPlayers: number
  lastError: string | null
}

export interface AuditEntry {
  id: string
  at: string
  actor: string
  action: string
  target: string
  detail: string
}

export interface Overview {
  serversOnline: number
  serversTotal: number
  playersOnline: number
  memoryBytes: number
  servers: Server[]
  recentActivity: AuditEntry[]
}

export interface Ban {
  id: string
  target: string
  displayName: string
  reason: string
  issuedBy: string
  issuedAt: string
  expiresAt: string | null
  revoked: boolean
}

export interface Schedule {
  id: string
  serverId: string
  name: string
  cron: string
  action: string
  enabled: boolean
  lastRunAt: string | null
}
