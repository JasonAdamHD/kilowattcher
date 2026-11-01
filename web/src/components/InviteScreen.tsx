import { useEffect, useRef, useState, type FormEvent } from 'react'
import { api } from '../api'
import { LogoMark } from './Icons'

const LOCKOUT_SECONDS = 60

type Props = {
  expired: boolean
  onRedeemed: () => void
}

export function InviteScreen({ expired, onRedeemed }: Props) {
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(expired ? 'Your session expired. Enter the invite code again.' : null)
  const [submitting, setSubmitting] = useState(false)
  const [locked, setLocked] = useState(false)
  const inputRef = useRef<HTMLInputElement>(null)

  // After too many tries, Continue stays disabled for 60 seconds.
  useEffect(() => {
    if (!locked) return
    const timer = setTimeout(() => {
      setLocked(false)
      setError(null)
    }, LOCKOUT_SECONDS * 1000)
    return () => clearTimeout(timer)
  }, [locked])

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!code.trim() || submitting || locked) return

    setSubmitting(true)
    const result = await api.redeem(code.trim())
    setSubmitting(false)

    if (result.ok) {
      onRedeemed()
      return
    }

    switch (result.kind) {
      case 'invalid_code':
      case 'bad_request':
        setError("That code didn't work. Check it and try again.")
        setCode('')
        inputRef.current?.focus()
        break
      case 'rate_limited':
        setError('Too many tries. Wait a minute and try again.')
        setLocked(true)
        break
      case 'offline':
        setError('You appear to be offline.')
        break
      default:
        setError('Something went wrong. Try again in a moment.')
    }
  }

  return (
    <main className="page page--center">
      <form className="card invite" onSubmit={submit} noValidate>
        <div className="invite__brand">
          <LogoMark />
          <h1>Kilowattcher</h1>
        </div>
        <p>Your home energy copilot. Ask about your battery, solar, and electricity rates.</p>

        <label className="field">
          <span className="field__label">Invite code</span>
          <input
            ref={inputRef}
            className="input"
            value={code}
            onChange={(e) => setCode(e.target.value)}
            autoComplete="off"
            autoCapitalize="off"
            spellCheck={false}
            autoFocus
            aria-invalid={error !== null && !expired}
            aria-describedby={error ? 'invite-error' : undefined}
          />
        </label>

        {error && (
          <p id="invite-error" className={expired && !locked ? 'notice' : 'error-text'} role="alert">
            {error}
          </p>
        )}

        <button className="button button--primary" type="submit" disabled={!code.trim() || submitting || locked}>
          {submitting ? 'Checking…' : 'Continue'}
        </button>

        <p className="helper">Please avoid entering personal information.</p>
      </form>
    </main>
  )
}
