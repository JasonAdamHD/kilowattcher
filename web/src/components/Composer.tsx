import { useId, type FormEvent, type KeyboardEvent } from 'react'
import { MAX_MESSAGE_LENGTH } from '../api'

type Props = {
  value: string
  onChange: (value: string) => void
  onSend: () => void
  busy: boolean
}

export function Composer({ value, onChange, onSend, busy }: Props) {
  const id = useId()
  const tooLong = value.length > MAX_MESSAGE_LENGTH
  const canSend = value.trim().length > 0 && !tooLong && !busy

  function submit(event: FormEvent) {
    event.preventDefault()
    if (canSend) onSend()
  }

  function onKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    // Enter sends, Shift+Enter adds a new line.
    if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault()
      if (canSend) onSend()
    }
  }

  return (
    <form className="composer" onSubmit={submit}>
      <label className="visually-hidden" htmlFor={id}>
        Your question
      </label>
      <textarea
        id={id}
        className="input composer__input"
        rows={1}
        placeholder="Ask about your battery, solar, or rates"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={onKeyDown}
        aria-invalid={tooLong}
        aria-describedby={`${id}-count`}
      />
      <div className="composer__row">
        <span id={`${id}-count`} className={tooLong ? 'counter counter--over' : 'counter'} aria-live="polite">
          {value.length} / {MAX_MESSAGE_LENGTH}
        </span>
        <button type="submit" className="button button--primary composer__send" disabled={!canSend}>
          Send
        </button>
      </div>
    </form>
  )
}
