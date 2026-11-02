import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react'
import { api, type Place, type SettingsOptions } from '../api'

type Props = {
  options: SettingsOptions
  /** The preset city id in use (ignored while `location` is set). */
  city: string
  location: Place | null
  /** The display name of the current home. */
  current: string
  onPickCity: (id: string) => void
  onPickPlace: (place: Place) => void
}

type Status =
  | { kind: 'idle' }
  | { kind: 'searching' }
  | { kind: 'results'; places: Place[] }
  | { kind: 'error'; text: string }

const MIN_QUERY = 2
const DEBOUNCE_MS = 300

/**
 * Where the home is: one-tap preset cities, or a search box for any US city name or ZIP code.
 * The search box is a combobox: type, then pick a match with the mouse or the arrow keys and Enter.
 */
export function LocationPicker({ options, city, location, current, onPickCity, onPickPlace }: Props) {
  const id = useId()
  const [query, setQuery] = useState('')
  const [status, setStatus] = useState<Status>({ kind: 'idle' })
  const [active, setActive] = useState(-1)
  const latest = useRef(0)

  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)

  // Stop a pending search when the panel unmounts.
  useEffect(() => () => clearTimeout(timer.current), [])

  /** Every keystroke cancels the previous search; one runs once typing pauses. */
  const search = (text: string) => {
    setQuery(text)
    clearTimeout(timer.current)
    const ticket = ++latest.current
    const q = text.trim()
    if (q.length < MIN_QUERY) {
      setStatus({ kind: 'idle' })
      return
    }

    setStatus({ kind: 'searching' })
    timer.current = setTimeout(async () => {
      const result = await api.searchLocations(q)
      if (ticket !== latest.current) return // a newer search started; drop this one
      setActive(-1)
      if (result.ok) setStatus({ kind: 'results', places: result.data.results })
      else if (result.kind === 'rate_limited') setStatus({ kind: 'error', text: 'Too many searches. Wait a moment and try again.' })
      else if (result.kind === 'bad_request') setStatus({ kind: 'error', text: 'Use letters and numbers only, like "Austin" or "78701".' })
      else setStatus({ kind: 'error', text: "Search isn't available right now. Try a city below." })
    }, DEBOUNCE_MS)
  }

  const places = status.kind === 'results' ? status.places : []
  const listOpen = places.length > 0

  const pick = (place: Place) => {
    onPickPlace(place)
    search('')
    setActive(-1)
  }

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown' && listOpen) {
      e.preventDefault()
      setActive((i) => (i + 1) % places.length)
    } else if (e.key === 'ArrowUp' && listOpen) {
      e.preventDefault()
      setActive((i) => (i <= 0 ? places.length - 1 : i - 1))
    } else if (e.key === 'Enter' && listOpen) {
      e.preventDefault()
      pick(places[active >= 0 ? active : 0])
    } else if (e.key === 'Escape' && (listOpen || query)) {
      // Clear the search first; a second Escape closes the panel.
      e.stopPropagation()
      search('')
    }
  }

  const message =
    status.kind === 'searching'
      ? 'Searching…'
      : status.kind === 'error'
        ? status.text
        : status.kind === 'results' && places.length === 0
          ? 'No US places match. Try a city name or a 5-digit ZIP code.'
          : null

  return (
    <div className="field location">
      <label className="field__label" htmlFor={`${id}-search`}>
        Home location
      </label>
      <p id={`${id}-current`} className="helper location__current" aria-live="polite">
        Weather and local time for <strong>{current}</strong>
      </p>

      <div className="location__combo">
        <input
          id={`${id}-search`}
          className="input"
          type="search"
          role="combobox"
          autoComplete="off"
          placeholder="City or ZIP code"
          maxLength={60}
          value={query}
          onChange={(e) => search(e.target.value)}
          onKeyDown={onKeyDown}
          aria-expanded={listOpen}
          aria-controls={`${id}-results`}
          aria-autocomplete="list"
          aria-activedescendant={listOpen && active >= 0 ? `${id}-result-${active}` : undefined}
          aria-describedby={`${id}-current${message ? ` ${id}-status` : ''}`}
        />
        <ul id={`${id}-results`} role="listbox" aria-label="Matching places" className="location__results" hidden={!listOpen}>
          {places.map((place, i) => (
            <li
              key={place.name}
              id={`${id}-result-${i}`}
              role="option"
              aria-selected={i === active}
              className={i === active ? 'location__result location__result--active' : 'location__result'}
              // mousedown, not click, so the pick lands before the input loses focus
              onMouseDown={(e) => {
                e.preventDefault()
                pick(place)
              }}
              onMouseEnter={() => setActive(i)}
            >
              {place.name}
            </li>
          ))}
        </ul>
      </div>
      {message && (
        <span id={`${id}-status`} className={status.kind === 'error' ? 'helper error-text' : 'helper'} role="status">
          {message}
        </span>
      )}

      <div className="location__presets" role="group" aria-label="Quick picks">
        {options.cities.map((c) => (
          <button
            key={c.id}
            type="button"
            className="chip"
            aria-pressed={!location && city === c.id}
            onClick={() => onPickCity(c.id)}
          >
            {c.city}
          </button>
        ))}
      </div>
    </div>
  )
}
