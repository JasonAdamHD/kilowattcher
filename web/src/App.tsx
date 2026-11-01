import { useCallback, useEffect, useState } from 'react'
import { api } from './api'
import type { Message } from './conversation'
import { ChatScreen } from './components/ChatScreen'
import { InviteScreen } from './components/InviteScreen'

type Screen = 'checking' | 'invite' | 'chat'

export default function App() {
  const [screen, setScreen] = useState<Screen>('checking')
  const [expired, setExpired] = useState(false)
  // A question that hit an expired session; it's resent once the visitor redeems again.
  const [pendingQuestion, setPendingQuestion] = useState<string | null>(null)
  // Kept here so the conversation survives a trip through the Invite screen.
  const [messages, setMessages] = useState<Message[]>([])

  useEffect(() => {
    let active = true
    api.session().then((result) => {
      if (active) setScreen(result.ok ? 'chat' : 'invite')
    })
    return () => {
      active = false
    }
  }, [])

  const handleRedeemed = useCallback(() => {
    setExpired(false)
    setScreen('chat')
  }, [])

  const handleSessionExpired = useCallback((unsentQuestion: string) => {
    setPendingQuestion(unsentQuestion)
    setExpired(true)
    setScreen('invite')
  }, [])

  const clearPending = useCallback(() => setPendingQuestion(null), [])

  if (screen === 'checking') {
    return (
      <main className="page page--center" aria-busy="true">
        <p className="muted">Loading…</p>
      </main>
    )
  }

  if (screen === 'invite') {
    return <InviteScreen expired={expired} onRedeemed={handleRedeemed} />
  }

  return (
    <ChatScreen
      messages={messages}
      setMessages={setMessages}
      pendingQuestion={pendingQuestion}
      onPendingQuestionSent={clearPending}
      onSessionExpired={handleSessionExpired}
    />
  )
}
