export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message)
  }
}

let requestReauthentication: (() => Promise<void>) | null = null
export const setReauthenticationHandler = (handler: (() => Promise<void>) | null) => { requestReauthentication = handler }

export async function api<T>(path: string, init?: RequestInit, retryAfterReauthentication = true): Promise<T> {
  const response = await fetch(`/api${path}`, {
    credentials: 'include',
    ...init,
    headers: { 'Content-Type': 'application/json', 'X-Panel-Request': '1', ...init?.headers },
  })
  const text = await response.text()
  let body: unknown
  if (text) {
    try { body = JSON.parse(text) }
    catch { body = text }
  }
  if (!response.ok) {
    const details = typeof body === 'object' && body !== null && 'issues' in body && Array.isArray((body as { issues?: unknown }).issues)
      ? (body as { issues: Array<{ severity?: string; message?: string }> }).issues
          .filter(issue => issue.severity === 'error' && issue.message)
          .map(issue => issue.message).join(' ')
      : ''
    const message = typeof body === 'object' && body !== null && 'error' in body
      ? `${String((body as { error: unknown }).error)}${details ? ` ${details}` : ''}`
      : typeof body === 'string' && body
        ? body
        : `Request failed (${response.status})`
    if (response.status === 428 && retryAfterReauthentication && requestReauthentication && path !== '/auth/reauthenticate') {
      await requestReauthentication()
      return api<T>(path, init, false)
    }
    throw new ApiError(response.status, message)
  }
  return body as T
}
