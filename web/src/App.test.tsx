import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import App from './App'

function stubFetch(impl: () => Promise<Response>) {
  const fetchMock = vi.fn(impl)
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

describe('App', () => {
  it('shows the app name', () => {
    stubFetch(() => new Promise(() => {}))
    render(<App />)
    expect(screen.getByRole('heading', { name: 'Kilowattcher' })).toBeInTheDocument()
  })

  it('reports the API as up when /api/health returns ok', async () => {
    const fetchMock = stubFetch(async () => Response.json({ status: 'ok' }))
    render(<App />)
    expect(await screen.findByText('API is up and running.')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledWith('/api/health', expect.anything())
  })

  it('reports the API as unreachable when the request fails', async () => {
    stubFetch(async () => {
      throw new TypeError('network down')
    })
    render(<App />)
    expect(await screen.findByText("Can't reach the API right now.")).toBeInTheDocument()
  })

  it('reports the API as unreachable on a server error', async () => {
    stubFetch(async () => new Response('bad gateway', { status: 502 }))
    render(<App />)
    expect(await screen.findByText("Can't reach the API right now.")).toBeInTheDocument()
  })
})
