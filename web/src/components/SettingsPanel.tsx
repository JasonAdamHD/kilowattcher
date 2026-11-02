import { useEffect, useId, useRef } from 'react'
import type { HouseholdSettings, SettingsOptions } from '../api'
import { homeName, priceError, toRequest, useSettings, type PriceField } from '../settings'
import { LocationPicker } from './LocationPicker'

type Props = {
  options: SettingsOptions
  open: boolean
  onClose: () => void
  /** Called with the settings to send (only what differs from the defaults). */
  onChange: (settings: HouseholdSettings | undefined) => void
}

const PRICE_FIELDS: { field: PriceField; label: string }[] = [
  { field: 'superOffPeakPrice', label: 'Super off-peak (12–6 AM)' },
  { field: 'offPeakPrice', label: 'Off-peak (6 AM–4 PM, 9 PM–12 AM)' },
  { field: 'peakPrice', label: 'Peak (4–9 PM)' },
]

/**
 * The household settings. On a wide screen it's a side panel that pops in and out on the right; on a
 * phone it's a sheet that slides up over the chat. The same controls either way; CSS decides the layout.
 */
export function SettingsPanel({ options, open, onClose, onChange }: Props) {
  const id = useId()
  const { values, update, reset } = useSettings(options)
  const closeRef = useRef<HTMLButtonElement>(null)
  const request = toRequest(values, options)
  const requestKey = JSON.stringify(request ?? null)

  useEffect(() => {
    onChange(request)
    // requestKey captures every change to the request; `request` itself is a new object each render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [requestKey, onChange])

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [open, onClose])

  const battery = options.batteries.find((b) => b.id === values.battery)

  return (
    <>
      {open && <div className="settings-backdrop" onClick={onClose} aria-hidden="true" />}
      <aside id="settings-panel" className="settings" aria-label="Household settings" hidden={!open}>
        <div className="settings__header">
          <h2>Your home</h2>
          <button ref={closeRef} type="button" className="button button--secondary button--small" onClick={onClose} aria-label="Close settings">
            Close
          </button>
        </div>

        <p className="helper">Changes apply to your next question. Saved in this browser.</p>

        <div className="field">
          <label className="field__label" htmlFor={`${id}-battery`}>
            Battery
          </label>
          <select
            id={`${id}-battery`}
            className="input"
            value={values.battery}
            onChange={(e) => update({ battery: e.target.value })}
            aria-describedby={`${id}-battery-help`}
          >
            {options.batteries.map((b) => (
              <option key={b.id} value={b.id}>
                {b.name} ({b.capacityKwh} kWh)
              </option>
            ))}
          </select>
          {battery && (
            <span id={`${id}-battery-help`} className="helper">
              Charges at up to {battery.maxChargeKw} kW, {Math.round(battery.roundTripEfficiency * 100)}% round-trip efficiency.
            </span>
          )}
        </div>

        <div className="field">
          <label className="field__label" htmlFor={`${id}-charge`}>
            Battery charge: <output htmlFor={`${id}-charge`}>{values.stateOfChargePercent}%</output>
          </label>
          <input
            id={`${id}-charge`}
            type="range"
            min={0}
            max={100}
            step={1}
            value={values.stateOfChargePercent}
            aria-valuetext={`${values.stateOfChargePercent}%`}
            onChange={(e) => update({ stateOfChargePercent: Number(e.target.value) })}
          />
        </div>

        <LocationPicker
          options={options}
          city={values.city}
          location={values.location}
          current={homeName(values, options)}
          onPickCity={(city) => update({ city, location: null })}
          onPickPlace={(location) => update({ location })}
        />

        <fieldset className="field settings__prices">
          <legend className="field__label">Electricity prices ($ per kWh)</legend>
          {PRICE_FIELDS.map(({ field, label }) => {
            const error = priceError(values[field], options.maxPrice)
            return (
              <div key={field} className="field">
                <label className="helper" htmlFor={`${id}-${field}`}>
                  {label}
                </label>
                <input
                  id={`${id}-${field}`}
                  className="input"
                  type="number"
                  inputMode="decimal"
                  min={0.01}
                  max={options.maxPrice}
                  step={0.01}
                  value={values[field]}
                  onChange={(e) => update({ [field]: e.target.value })}
                  aria-invalid={error !== null}
                  aria-describedby={error ? `${id}-${field}-error` : undefined}
                />
                {error && (
                  <span id={`${id}-${field}-error`} className="error-text helper">
                    {error} The default is used until it's fixed.
                  </span>
                )}
              </div>
            )
          })}
        </fieldset>

        <button type="button" className="button button--secondary" onClick={reset} disabled={!request}>
          Reset to defaults
        </button>
      </aside>
    </>
  )
}
