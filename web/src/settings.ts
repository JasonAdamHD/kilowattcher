import { useCallback, useState } from 'react'
import type { HouseholdSettings, SettingsOptions } from './api'

/** What the panel shows. Prices are kept as typed text so a half-typed "0." isn't lost. */
export type SettingsValues = {
  battery: string
  stateOfChargePercent: number
  city: string
  superOffPeakPrice: string
  offPeakPrice: string
  peakPrice: string
}

export type PriceField = 'superOffPeakPrice' | 'offPeakPrice' | 'peakPrice'

const STORAGE_KEY = 'kilowattcher.settings.v1'

export function defaultValues(options: SettingsOptions): SettingsValues {
  const d = options.defaults
  return {
    battery: d.battery,
    stateOfChargePercent: d.stateOfChargePercent,
    city: d.city ?? options.cities[0]?.id ?? '',
    superOffPeakPrice: d.superOffPeakPrice.toFixed(2),
    offPeakPrice: d.offPeakPrice.toFixed(2),
    peakPrice: d.peakPrice.toFixed(2),
  }
}

/** A price is valid when it's a number above $0 and at most the server's maximum. */
export function priceError(text: string, max: number): string | null {
  const value = Number(text)
  if (text.trim() === '' || !Number.isFinite(value) || value <= 0 || value > max) {
    return `Enter a price from $0.01 to $${max.toFixed(2)}.`
  }
  return null
}

/**
 * Only what differs from the defaults goes to the server, so it can tell the model "the visitor changed
 * these". Invalid prices are left out (the default applies) and the panel shows why.
 */
export function toRequest(values: SettingsValues, options: SettingsOptions): HouseholdSettings | undefined {
  const d = defaultValues(options)
  const out: HouseholdSettings = {}
  if (values.battery !== d.battery) out.battery = values.battery
  if (values.stateOfChargePercent !== d.stateOfChargePercent) out.stateOfChargePercent = values.stateOfChargePercent
  if (values.city !== d.city) out.city = values.city
  for (const field of ['superOffPeakPrice', 'offPeakPrice', 'peakPrice'] as const) {
    if (!priceError(values[field], options.maxPrice) && Number(values[field]) !== Number(d[field])) {
      out[field] = Math.round(Number(values[field]) * 100) / 100
    }
  }
  return Object.keys(out).length > 0 ? out : undefined
}

function load(options: SettingsOptions): SettingsValues {
  const defaults = defaultValues(options)
  try {
    const saved = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? 'null') as Partial<SettingsValues> | null
    if (!saved) return defaults
    // Ignore anything the server no longer offers (a removed model or city).
    return {
      ...defaults,
      ...saved,
      battery: options.batteries.some((b) => b.id === saved.battery) ? saved.battery! : defaults.battery,
      city: options.cities.some((c) => c.id === saved.city) ? saved.city! : defaults.city,
      stateOfChargePercent:
        typeof saved.stateOfChargePercent === 'number' && saved.stateOfChargePercent >= 0 && saved.stateOfChargePercent <= 100
          ? saved.stateOfChargePercent
          : defaults.stateOfChargePercent,
    }
  } catch {
    return defaults
  }
}

function save(values: SettingsValues) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(values))
  } catch {
    // Private mode or storage disabled: settings still work for this visit.
  }
}

/** Panel values, remembered in this browser between visits. */
export function useSettings(options: SettingsOptions) {
  const [values, setValues] = useState<SettingsValues>(() => load(options))

  const update = useCallback((change: Partial<SettingsValues>) => {
    setValues((current) => {
      const next = { ...current, ...change }
      save(next)
      return next
    })
  }, [])

  const reset = useCallback(() => {
    const defaults = defaultValues(options)
    save(defaults)
    setValues(defaults)
  }, [options])

  return { values, update, reset }
}
