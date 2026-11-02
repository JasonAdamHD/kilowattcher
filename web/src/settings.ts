import { useCallback, useState } from 'react'
import type { HouseholdSettings, Place, SettingsOptions } from './api'

/** What the panel shows. Prices are kept as typed text so a half-typed "0." isn't lost. */
export type SettingsValues = {
  battery: string
  stateOfChargePercent: number
  /** Battery drain in kW, kept within the chosen model's max discharge. */
  loadKw: number
  /** The preset city id; ignored while a searched location is set. */
  city: string
  /** A place from the search box, or null to use the preset city. */
  location: Place | null
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
    loadKw: d.loadKw,
    city: d.city ?? options.cities[0]?.id ?? '',
    location: null,
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
  if (values.loadKw !== d.loadKw) out.loadKw = values.loadKw
  if (values.location) out.location = values.location
  else if (values.city !== d.city) out.city = values.city
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
    const battery = options.batteries.some((b) => b.id === saved.battery) ? saved.battery! : defaults.battery
    return {
      ...defaults,
      ...saved,
      battery,
      loadKw: typeof saved.loadKw === 'number' && Number.isFinite(saved.loadKw) ? clampLoad(saved.loadKw, battery, options) : defaults.loadKw,
      city: options.cities.some((c) => c.id === saved.city) ? saved.city! : defaults.city,
      location: isPlace(saved.location) ? saved.location : null,
      stateOfChargePercent:
        typeof saved.stateOfChargePercent === 'number' && saved.stateOfChargePercent >= 0 && saved.stateOfChargePercent <= 100
          ? saved.stateOfChargePercent
          : defaults.stateOfChargePercent,
    }
  } catch {
    return defaults
  }
}

/** The most this battery can discharge, so the drain slider never goes past it. */
export function maxLoadKw(battery: string, options: SettingsOptions): number {
  return options.batteries.find((b) => b.id === battery)?.maxDischargeKw ?? options.defaults.loadKw
}

/** Keeps the drain within the battery's limit, on the slider's 0.1 kW steps. */
export function clampLoad(load: number, battery: string, options: SettingsOptions): number {
  return Math.min(Math.max(0, Math.round(load * 10) / 10), maxLoadKw(battery, options))
}

/** A saved place must have every field; the server checks its signature on each question. */
function isPlace(value: unknown): value is Place {
  if (typeof value !== 'object' || value === null) return false
  const p = value as Record<string, unknown>
  return (
    typeof p.name === 'string' &&
    typeof p.latitude === 'number' &&
    typeof p.longitude === 'number' &&
    typeof p.timeZone === 'string' &&
    typeof p.signature === 'string'
  )
}

/** The home's name for display: the searched place, else the preset city. */
export function homeName(values: SettingsValues, options: SettingsOptions): string {
  return values.location?.name ?? options.cities.find((c) => c.id === values.city)?.city ?? options.defaults.cityName
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
