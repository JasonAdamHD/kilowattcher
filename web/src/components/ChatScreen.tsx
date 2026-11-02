import { useCallback, useEffect, useRef, useState, type Dispatch, type SetStateAction } from 'react'
import { api, type ApiFailure, type Citation, type HouseholdSettings, type SettingsOptions } from '../api'
import { newId, STARTER_QUESTIONS, toHistory, type Message } from '../conversation'
import { AssistantReply } from './AssistantReply'
import { CitationOverlay } from './CitationOverlay'
import { Composer } from './Composer'
import { GearIcon, LogoMark } from './Icons'
import { SettingsPanel } from './SettingsPanel'

type Failure = { kind: ApiFailure['kind']; question: string; retryAfterSeconds?: number; error?: string }

type Props = {
  messages: Message[]
  setMessages: Dispatch<SetStateAction<Message[]>>
  pendingQuestion: string | null
  onPendingQuestionSent: () => void
  onSessionExpired: (unsentQuestion: string) => void
}

export function ChatScreen({ messages, setMessages, pendingQuestion, onPendingQuestionSent, onSessionExpired }: Props) {
  const [draft, setDraft] = useState('')
  const [busy, setBusy] = useState(false)
  const [failure, setFailure] = useState<Failure | null>(null)
  const [citation, setCitation] = useState<Citation | null>(null)
  const citationTrigger = useRef<HTMLElement | null>(null)
  const endRef = useRef<HTMLDivElement>(null)

  // Household settings: options come from the API; the panel reports what to send with each question.
  const [options, setOptions] = useState<SettingsOptions | null>(null)
  const [settingsOpen, setSettingsOpen] = useState(() => window.matchMedia?.('(min-width: 900px)').matches ?? false)
  const settingsRef = useRef<HouseholdSettings | undefined>(undefined)
  const settingsButton = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    let active = true
    api.settingsOptions().then((result) => {
      if (active && result.ok) setOptions(result.data)
    })
    return () => {
      active = false
    }
  }, [])

  const onSettingsChange = useCallback((settings: HouseholdSettings | undefined) => {
    settingsRef.current = settings
  }, [])

  const closeSettings = useCallback(() => {
    setSettingsOpen(false)
    settingsButton.current?.focus()
  }, [])

  useEffect(() => {
    endRef.current?.scrollIntoView?.({ block: 'end' })
  }, [messages, busy, failure])

  /** Sends a question. On retry, the visitor's bubble is already on screen, so it isn't added twice. */
  const send = useCallback(
    async (question: string, { retry = false } = {}) => {
      const before = retry ? messages.slice(0, -1) : messages
      if (!retry) {
        setMessages([...before, { id: newId(), role: 'user', text: question }])
      }

      setFailure(null)
      setBusy(true)
      const result = await api.chat(question, toHistory(before), settingsRef.current)
      setBusy(false)

      if (result.ok) {
        setMessages((current) => [...current, { id: newId(), role: 'assistant', reply: result.data }])
        return
      }

      if (result.kind === 'unauthorized') {
        // Drop the unanswered bubble; the question is resent after the visitor redeems again.
        setMessages(before)
        onSessionExpired(question)
        return
      }

      if (result.kind === 'invalid_answer') {
        setDraft(question)
      }

      setFailure({
        kind: result.kind,
        question,
        retryAfterSeconds: result.kind === 'rate_limited' ? result.retryAfterSeconds : undefined,
        error: result.kind === 'bad_request' ? result.error : undefined,
      })
    },
    [messages, setMessages, onSessionExpired],
  )

  // Resend the question that was interrupted by an expired session.
  const resentRef = useRef(false)
  useEffect(() => {
    if (pendingQuestion && !resentRef.current) {
      resentRef.current = true
      onPendingQuestionSent()
      void send(pendingQuestion)
    }
  }, [pendingQuestion, onPendingQuestionSent, send])

  function sendDraft() {
    const question = draft.trim()
    setDraft('')
    void send(question)
  }

  function ask(question: string) {
    if (!busy) void send(question)
  }

  function newChat() {
    setMessages([])
    setFailure(null)
    setDraft('')
  }

  function openCitation(next: Citation, trigger: HTMLElement) {
    citationTrigger.current = trigger
    setCitation(next)
  }

  const closeCitation = useCallback(() => {
    setCitation(null)
    citationTrigger.current?.focus()
  }, [])

  return (
    <div className="chat">
      <header className="header">
        <div className="header__inner">
          <div className="header__brand">
            <LogoMark />
            <span className="header__name">Kilowattcher</span>
          </div>
          <div className="header__actions">
            {options && (
              <button
                ref={settingsButton}
                type="button"
                className="button button--secondary button--small header__settings"
                aria-expanded={settingsOpen}
                aria-controls="settings-panel"
                onClick={() => setSettingsOpen((open) => !open)}
              >
                <GearIcon />
                <span className="header__settings-label">Settings</span>
              </button>
            )}
            <button type="button" className="button button--secondary button--small" onClick={newChat} disabled={busy}>
              New chat
            </button>
          </div>
        </div>
      </header>

      <div className={settingsOpen ? 'chat__layout chat__layout--with-settings' : 'chat__layout'}>
        <div className="chat__main">
          <main className="chat__body">
            <section className="messages" aria-live="polite" aria-label="Conversation">
              {messages.length === 0 && !busy && (
                <div className="starters">
                  <h1>What can I help with?</h1>
                  <p className="muted">Answers use your battery, rate plan, solar forecast, and documents.</p>
                  <ul>
                    {STARTER_QUESTIONS.map((q) => (
                      <li key={q}>
                        <button type="button" className="starter" onClick={() => ask(q)}>
                          {q}
                        </button>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
    
              {messages.map((message) =>
                message.role === 'user' ? (
                  <p key={message.id} className="bubble bubble--user">
                    {message.text}
                  </p>
                ) : (
                  <AssistantReply
                    key={message.id}
                    messageId={message.id}
                    reply={message.reply}
                    onOpenCitation={openCitation}
                    onAsk={ask}
                  />
                ),
              )}
    
              {busy && (
                <div className="loading" role="status">
                  <span className="dots" aria-hidden="true">
                    <span />
                    <span />
                    <span />
                  </span>
                  Checking your battery and rates…
                </div>
              )}
    
              {failure && <ErrorCard failure={failure} onRetry={() => void send(failure.question, { retry: true })} />}
              <div ref={endRef} />
            </section>
          </main>
    
          <footer className="chat__footer">
            <Composer value={draft} onChange={setDraft} onSend={sendDraft} busy={busy} />
            <p className="attribution">
              Solar forecasts use weather data by{' '}
              <a href="https://open-meteo.com/" target="_blank" rel="noreferrer">
                Open-Meteo.com
              </a>{' '}
              (CC BY 4.0)
            </p>
          </footer>
        </div>

        {options && (
          <SettingsPanel options={options} open={settingsOpen} onClose={closeSettings} onChange={onSettingsChange} />
        )}
      </div>

      {citation && <CitationOverlay citation={citation} onClose={closeCitation} />}
    </div>
  )
}

function ErrorCard({ failure, onRetry }: { failure: Failure; onRetry: () => void }) {
  const { text, canRetry } = describeFailure(failure)
  return (
    <div className="error-card" role="alert">
      <p>{text}</p>
      {canRetry && (
        <button type="button" className="button button--secondary button--small" onClick={onRetry}>
          Retry
        </button>
      )}
    </div>
  )
}

function describeFailure(failure: Failure): { text: string; canRetry: boolean } {
  switch (failure.kind) {
    case 'busy':
      return { text: 'The assistant is busy. Try again in a minute.', canRetry: true }
    case 'rate_limited': {
      const s = failure.retryAfterSeconds ?? 60
      return { text: `You're asking quickly. Try again in ${s} second${s === 1 ? '' : 's'}.`, canRetry: true }
    }
    case 'invalid_answer':
      return { text: "I couldn't put that answer together. Try rephrasing.", canRetry: false }
    case 'offline':
      return { text: 'You appear to be offline.', canRetry: true }
    case 'bad_request':
      if (failure.error && ['unknown_battery', 'invalid_charge', 'unknown_city', 'invalid_location', 'invalid_price'].includes(failure.error)) {
        return { text: "Some settings aren't valid. Check the settings panel, or reset to defaults.", canRetry: false }
      }
      return failure.error === 'invalid_history'
        ? { text: "This conversation can't continue. Tap New chat to start again.", canRetry: false }
        : { text: "That question couldn't be sent. Try a shorter one.", canRetry: false }
    default:
      return { text: 'Something went wrong reaching the assistant.', canRetry: true }
  }
}
