// Typed client for the Kilowattcher API. Every call returns a result object instead of throwing,
// so screens can map each failure to the wording in docs/03-app-flow.md.

export type ToolCall = {
  name: string
  arguments: Record<string, unknown>
  result: unknown
}

export type Citation = {
  id: string
  title: string
  heading: string
  text: string
}

export type ChatReply = {
  topic: 'home_energy' | 'off_topic' | 'safety' | 'blocked'
  answer: string
  toolCalls: ToolCall[]
  citations: Citation[]
  suggestedQuestion: string | null
  /** Proof the API wrote this answer; sent back with it in history. */
  signature: string
}

export type HistoryMessage =
  | { role: 'user'; content: string }
  | { role: 'assistant'; content: string; signature: string }

/** What the settings panel can change. Fields left out keep the household defaults. */
export type HouseholdSettings = {
  battery?: string
  stateOfChargePercent?: number
  city?: string
  superOffPeakPrice?: number
  offPeakPrice?: number
  peakPrice?: number
}

export type SettingsOptions = {
  batteries: { id: string; name: string; capacityKwh: number; maxChargeKw: number; roundTripEfficiency: number }[]
  cities: { id: string; city: string }[]
  defaults: {
    battery: string
    stateOfChargePercent: number
    city: string | null
    cityName: string
    superOffPeakPrice: number
    offPeakPrice: number
    peakPrice: number
  }
  maxPrice: number
}

export type ApiFailure =
  | { kind: 'unauthorized' }
  | { kind: 'invalid_code' }
  | { kind: 'rate_limited'; retryAfterSeconds: number }
  | { kind: 'busy' }
  | { kind: 'unavailable' }
  | { kind: 'invalid_answer' }
  | { kind: 'bad_request'; error: string }
  | { kind: 'offline' }

export type ApiResult<T> = { ok: true; data: T } | ({ ok: false } & ApiFailure)

/** The API caps history at 10 messages; keep the most recent ones. */
export const MAX_HISTORY = 10
export const MAX_MESSAGE_LENGTH = 1000

// The agent can make several model calls (60 s server budget); give it a little longer before giving up.
const CHAT_TIMEOUT_MS = 70_000
const DEFAULT_TIMEOUT_MS = 15_000

async function request<T>(path: string, init: RequestInit, timeoutMs: number): Promise<ApiResult<T>> {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)

  let response: Response
  try {
    response = await fetch(path, {
      ...init,
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', ...init.headers },
      signal: controller.signal,
    })
  } catch {
    if (controller.signal.aborted) return { ok: false, kind: 'unavailable' }
    return { ok: false, kind: navigator.onLine === false ? 'offline' : 'unavailable' }
  } finally {
    clearTimeout(timer)
  }

  if (response.ok) {
    return { ok: true, data: (await response.json()) as T }
  }

  const error = await readErrorCode(response)
  switch (response.status) {
    case 401:
      return error === 'invalid_code' ? { ok: false, kind: 'invalid_code' } : { ok: false, kind: 'unauthorized' }
    case 429:
      if (error === 'rate_limited') {
        const seconds = Number(response.headers.get('Retry-After'))
        return { ok: false, kind: 'rate_limited', retryAfterSeconds: Number.isFinite(seconds) && seconds > 0 ? seconds : 60 }
      }
      return { ok: false, kind: 'busy' }
    case 400:
      return { ok: false, kind: 'bad_request', error: error ?? 'bad_request' }
    case 502:
      return error === 'invalid_answer' ? { ok: false, kind: 'invalid_answer' } : { ok: false, kind: 'unavailable' }
    default:
      return { ok: false, kind: 'unavailable' }
  }
}

async function readErrorCode(response: Response): Promise<string | null> {
  try {
    const body: unknown = await response.json()
    if (typeof body === 'object' && body !== null && 'error' in body && typeof body.error === 'string') {
      return body.error
    }
  } catch {
    // Not JSON (for example a proxy error page).
  }
  return null
}

export const api = {
  session: () => request<{ authenticated: boolean; expiresAt: string }>('/api/auth/session', { method: 'GET' }, DEFAULT_TIMEOUT_MS),

  redeem: (code: string) =>
    request<{ authenticated: boolean; expiresAt: string }>(
      '/api/auth/redeem',
      { method: 'POST', body: JSON.stringify({ code }) },
      DEFAULT_TIMEOUT_MS,
    ),

  settingsOptions: () => request<SettingsOptions>('/api/settings/options', { method: 'GET' }, DEFAULT_TIMEOUT_MS),

  chat: (message: string, history: HistoryMessage[], settings?: HouseholdSettings) =>
    request<ChatReply>(
      '/api/chat',
      { method: 'POST', body: JSON.stringify({ message, history: history.slice(-MAX_HISTORY), settings }) },
      CHAT_TIMEOUT_MS,
    ),
}
