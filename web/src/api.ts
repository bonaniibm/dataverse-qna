// Thin client for the local Dataverse QnA host. The per-launch token arrives in the URL (?t=...).

const TOKEN_KEY = 'dvqna-token'

function token(): string {
  const fromUrl = new URLSearchParams(window.location.search).get('t')
  if (fromUrl) {
    try { sessionStorage.setItem(TOKEN_KEY, fromUrl) } catch { /* storage may be blocked */ }
    window.history.replaceState(null, '', window.location.pathname)
    return fromUrl
  }
  try { return sessionStorage.getItem(TOKEN_KEY) ?? '' } catch { return '' }
}

const TOKEN = token()

export type Row = Record<string, unknown>

export interface Source { env: string; kind: string; query?: string; window?: string; fetchedAt: string }

export interface EnvironmentProfile {
  name: string; url: string; tenantId: string; tags: string[]
  appInsightsResourceId?: string; privacyMode: string
}

export interface Status {
  llmReady: boolean; llmStatus: string; authMode: string; environments: number
  tools: { name: string; description: string }[]
}

export interface ToolRun {
  summary: string; rowCount: number; datasetId?: string; warnings: string[]
  sources: Source[]; ui?: { type: string; title?: string }; rows: Row[]
}

export interface Dataset { info: { id: string; tool: string; env: string; rowCount: number; fetchedAt: string }; rows: Row[]; sources: Source[] }

export interface StoredMessage { role: string; content: string; at: string }
export interface Conversation { id: string; title: string; updatedAt: string }

async function call<T>(path: string, init: RequestInit = {}): Promise<T> {
  const res = await fetch(path, {
    ...init,
    headers: { 'Content-Type': 'application/json', 'X-DvQna-Token': TOKEN, ...(init.headers ?? {}) },
  })
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`
    try { const body = await res.json(); message = body.error ?? body.title ?? message } catch { /* not JSON */ }
    throw new Error(message)
  }
  if (res.status === 204) return undefined as T
  return res.json() as Promise<T>
}

export const api = {
  hasToken: () => TOKEN.length > 0,
  status: () => call<Status>('/api/status'),
  environments: () => call<EnvironmentProfile[]>('/api/environments'),
  addEnvironment: (body: { name: string; url: string; tags: string[]; appInsightsResourceId?: string }) =>
    call<EnvironmentProfile>('/api/environments', { method: 'POST', body: JSON.stringify(body) }),
  removeEnvironment: (name: string) => call<void>(`/api/environments/${encodeURIComponent(name)}`, { method: 'DELETE' }),
  runTool: (name: string, input: Record<string, unknown>) =>
    call<ToolRun>(`/api/tools/${name}`, { method: 'POST', body: JSON.stringify(input) }),
  dataset: (id: string) => call<Dataset>(`/api/datasets/${id}`),
  conversations: () => call<Conversation[]>('/api/conversations'),
  conversation: (id: string) => call<StoredMessage[]>(`/api/conversations/${id}`),
}

export interface ChatEvent { type: string; data: Record<string, unknown> }

/** POSTs a chat message and yields server-sent events as they arrive. */
export async function* chat(body: { conversationId?: string; message: string; env?: string; compareEnv?: string; deep?: boolean }, signal: AbortSignal): AsyncGenerator<ChatEvent> {
  const res = await fetch('/api/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-DvQna-Token': TOKEN },
    body: JSON.stringify(body),
    signal,
  })
  if (!res.ok || !res.body) {
    let message = `${res.status} ${res.statusText}`
    try { const b = await res.json(); message = b.error ?? message } catch { /* ignore */ }
    throw new Error(message)
  }
  const reader = res.body.pipeThrough(new TextDecoderStream()).getReader()
  let buffer = ''
  for (;;) {
    const { value, done } = await reader.read()
    if (done) break
    buffer += value
    let idx: number
    while ((idx = buffer.indexOf('\n\n')) >= 0) {
      const raw = buffer.slice(0, idx)
      buffer = buffer.slice(idx + 2)
      let type = 'message'
      let data = ''
      for (const line of raw.split('\n')) {
        if (line.startsWith('event: ')) type = line.slice(7)
        else if (line.startsWith('data: ')) data += line.slice(6)
      }
      yield { type, data: data ? JSON.parse(data) : {} }
    }
  }
}
