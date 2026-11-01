import { useEffect, useRef } from 'react'
import type { Citation } from '../api'

type Props = {
  citation: Citation
  onClose: () => void
}

/** Shows the cited passage. The page behind keeps its scroll position because nothing scrolls it. */
export function CitationOverlay({ citation, onClose }: Props) {
  const closeRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    closeRef.current?.focus()
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="overlay" onClick={onClose}>
      <div
        className="card overlay__panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby="citation-title"
        onClick={(e) => e.stopPropagation()}
      >
        <p className="helper">{citation.title}</p>
        <h2 id="citation-title">{citation.heading}</h2>
        <p className="overlay__text">{citation.text}</p>
        <button ref={closeRef} type="button" className="button button--secondary" onClick={onClose}>
          Close
        </button>
      </div>
    </div>
  )
}
