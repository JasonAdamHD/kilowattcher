import { useEffect, useState } from 'react'

type ApiStatus = 'checking' | 'ok' | 'unreachable'

const statusText: Record<ApiStatus, string> = {
  checking: 'Checking the API…',
  ok: 'API is up and running.',
  unreachable: "Can't reach the API right now.",
}

export default function App() {
  const [status, setStatus] = useState<ApiStatus>('checking')

  useEffect(() => {
    const controller = new AbortController()

    fetch('/api/health', { signal: controller.signal })
      .then(async (response) => {
        const body: unknown = response.ok ? await response.json() : null
        const isOk =
          typeof body === 'object' && body !== null && 'status' in body && body.status === 'ok'
        setStatus(isOk ? 'ok' : 'unreachable')
      })
      .catch(() => {
        if (!controller.signal.aborted) setStatus('unreachable')
      })

    return () => controller.abort()
  }, [])

  return (
    <main className="page">
      <h1>Kilowattcher</h1>
      <p>Your home energy copilot is on its way.</p>
      <p className={`status status--${status}`} role="status">
        {statusText[status]}
      </p>
    </main>
  )
}
