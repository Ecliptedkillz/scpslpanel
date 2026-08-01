export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message)
  }
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
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
    const message = typeof body === 'object' && body !== null && 'error' in body
      ? String((body as { error: unknown }).error)
      : typeof body === 'string' && body
        ? body
        : `Request failed (${response.status})`
    throw new ApiError(response.status, message)
  }
  return body as T
}
