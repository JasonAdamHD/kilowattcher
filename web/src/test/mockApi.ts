import { vi } from 'vitest'
import type { ChatReply } from '../api'

type Handler = (body: unknown) => Response | Promise<Response>

export type ApiCall = { method: string; path: string; body: unknown }

/**
 * Replaces fetch with a tiny router. Handlers are queues per "METHOD /path": each call takes the
 * next one, and the last one repeats. Every request is recorded for assertions.
 */
export function mockApi(routes: Record<string, Handler | Handler[]>) {
  const calls: ApiCall[] = []
  const queues = new Map(Object.entries(routes).map(([key, h]) => [key, Array.isArray(h) ? [...h] : [h]]))

  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    const path = typeof input === 'string' ? input : input.toString()
    const body = init?.body ? JSON.parse(String(init.body)) : undefined
    calls.push({ method, path, body })

    const queue = queues.get(`${method} ${path}`)
    if (!queue || queue.length === 0) throw new Error(`No mock for ${method} ${path}`)
    const handler = queue.length > 1 ? queue.shift()! : queue[0]
    return handler(body)
  })

  vi.stubGlobal('fetch', fetchMock)
  return { calls, chatCalls: () => calls.filter((c) => c.path === '/api/chat') }
}

export const json = (status: number, body: unknown, headers: Record<string, string> = {}) => () =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } })

export const signedIn = json(200, { authenticated: true, expiresAt: '2026-11-02T20:00:00Z' })
export const signedOut = json(401, { error: 'unauthorized' })

export const reply = (overrides: Partial<ChatReply> = {}) =>
  json(200, {
    answer: 'Yes. Charge between midnight and 6 AM at $0.11 per kWh.',
    toolCalls: [],
    citations: [],
    suggestedQuestion: null,
    ...overrides,
  } satisfies ChatReply)

export const networkDown = () => {
  throw new TypeError('Failed to fetch')
}
