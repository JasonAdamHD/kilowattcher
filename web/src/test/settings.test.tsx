import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import App from '../App'
import type { SettingsOptions } from '../api'
import { priceError, toRequest, defaultValues } from '../settings'
import { json, mockApi, reply, signedIn } from './mockApi'

const OPTIONS: SettingsOptions = {
  batteries: [
    { id: 'voltnest-13', name: 'VoltNest 13', capacityKwh: 13.5, maxChargeKw: 5, roundTripEfficiency: 0.9 },
    { id: 'helio-powercell-10', name: 'Helio PowerCell 10', capacityKwh: 10, maxChargeKw: 3.8, roundTripEfficiency: 0.88 },
  ],
  cities: [
    { id: 'phoenix', city: 'Phoenix, AZ' },
    { id: 'seattle', city: 'Seattle, WA' },
  ],
  defaults: {
    battery: 'voltnest-13',
    stateOfChargePercent: 42,
    city: 'phoenix',
    cityName: 'Phoenix, AZ',
    superOffPeakPrice: 0.11,
    offPeakPrice: 0.24,
    peakPrice: 0.48,
  },
  maxPrice: 2,
}

const CHARGE = 'Should I charge my battery from the grid tonight?'

async function openChat(extraRoutes = {}) {
  const api = mockApi({
    'GET /api/auth/session': signedIn,
    'GET /api/settings/options': json(200, OPTIONS),
    'POST /api/chat': reply(),
    ...extraRoutes,
  })
  const user = userEvent.setup()
  render(<App />)
  await screen.findByRole('button', { name: 'Settings' })
  return { api, user }
}

const panel = () => screen.queryByRole('complementary', { name: 'Household settings' })

describe('Settings panel', () => {
  beforeEach(() => localStorage.clear())
  afterEach(() => localStorage.clear())

  it('opens and closes from the header button, with Escape, and from Close', async () => {
    const { user } = await openChat()
    const toggle = screen.getByRole('button', { name: 'Settings' })

    expect(panel()).not.toBeInTheDocument()
    expect(toggle).toHaveAttribute('aria-expanded', 'false')

    await user.click(toggle)
    expect(panel()).toBeInTheDocument()
    expect(toggle).toHaveAttribute('aria-expanded', 'true')

    await user.keyboard('{Escape}')
    expect(panel()).not.toBeInTheDocument()
    expect(toggle).toHaveFocus()

    await user.click(toggle)
    await user.click(screen.getByRole('button', { name: 'Close settings' }))
    expect(panel()).not.toBeInTheDocument()
  })

  it('shows the defaults from the API', async () => {
    const { user } = await openChat()
    await user.click(screen.getByRole('button', { name: 'Settings' }))

    expect(screen.getByLabelText('Battery')).toHaveValue('voltnest-13')
    expect(screen.getByText('42%')).toBeInTheDocument()
    expect(screen.getByLabelText('City')).toHaveValue('phoenix')
    expect(screen.getByLabelText('Peak (4–9 PM)')).toHaveValue(0.48)
    expect(screen.getByRole('button', { name: 'Reset to defaults' })).toBeDisabled()
  })

  it('sends nothing extra while everything is at its default', async () => {
    const { api, user } = await openChat()

    await user.click(screen.getByRole('button', { name: CHARGE }))
    await screen.findByText(/Charge between midnight/)

    expect(api.chatCalls()[0].body).toEqual({ message: CHARGE, history: [] })
  })

  it('sends only the settings that differ from the defaults', async () => {
    const { api, user } = await openChat()
    await user.click(screen.getByRole('button', { name: 'Settings' }))

    await user.selectOptions(screen.getByLabelText('Battery'), 'helio-powercell-10')
    fireEvent.change(screen.getByRole('slider'), { target: { value: '30' } })
    await user.selectOptions(screen.getByLabelText('City'), 'seattle')
    await user.clear(screen.getByLabelText('Peak (4–9 PM)'))
    await user.type(screen.getByLabelText('Peak (4–9 PM)'), '0.55')

    expect(screen.getByText('30%')).toBeInTheDocument()
    expect(screen.getByText(/3.8 kW, 88% round-trip efficiency/)).toBeInTheDocument()

    await user.type(screen.getByLabelText('Your question'), 'Charge tonight?{Enter}')
    await screen.findByText(/Charge between midnight/)

    expect(api.chatCalls()[0].body).toEqual({
      message: 'Charge tonight?',
      history: [],
      settings: { battery: 'helio-powercell-10', stateOfChargePercent: 30, city: 'seattle', peakPrice: 0.55 },
    })
  })

  it('flags an invalid price and keeps the default for it', async () => {
    const { api, user } = await openChat()
    await user.click(screen.getByRole('button', { name: 'Settings' }))

    await user.clear(screen.getByLabelText('Super off-peak (12–6 AM)'))
    await user.type(screen.getByLabelText('Super off-peak (12–6 AM)'), '5')

    expect(screen.getByLabelText('Super off-peak (12–6 AM)')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByText(/Enter a price from \$0.01 to \$2.00/)).toBeInTheDocument()

    await user.type(screen.getByLabelText('Your question'), 'hi{Enter}')
    await screen.findByText(/Charge between midnight/)
    expect(api.chatCalls()[0].body).toEqual({ message: 'hi', history: [] })
  })

  it('remembers choices in this browser and resets to defaults', async () => {
    const first = await openChat()
    await first.user.click(screen.getByRole('button', { name: 'Settings' }))
    await first.user.selectOptions(screen.getByLabelText('Battery'), 'helio-powercell-10')
    expect(localStorage.getItem('kilowattcher.settings.v1')).toContain('helio-powercell-10')

    // A reload: a fresh App reads the saved choice.
    document.body.innerHTML = ''
    const second = await openChat()
    await second.user.click(screen.getByRole('button', { name: 'Settings' }))
    expect(screen.getByLabelText('Battery')).toHaveValue('helio-powercell-10')

    await second.user.click(screen.getByRole('button', { name: 'Reset to defaults' }))
    expect(screen.getByLabelText('Battery')).toHaveValue('voltnest-13')
    expect(screen.getByRole('button', { name: 'Reset to defaults' })).toBeDisabled()
  })

  it('ignores a saved model the server no longer offers', async () => {
    localStorage.setItem('kilowattcher.settings.v1', JSON.stringify({ battery: 'retired-model', stateOfChargePercent: 250 }))
    const { user } = await openChat()
    await user.click(screen.getByRole('button', { name: 'Settings' }))

    expect(screen.getByLabelText('Battery')).toHaveValue('voltnest-13')
    expect(screen.getByText('42%')).toBeInTheDocument()
  })

  it('explains when the server rejects the settings', async () => {
    const { user } = await openChat({ 'POST /api/chat': json(400, { error: 'unknown_battery' }) })

    await user.click(screen.getByRole('button', { name: CHARGE }))

    expect(await screen.findByText(/Some settings aren't valid/)).toBeInTheDocument()
  })

  it('hides the Settings button when options are unavailable', async () => {
    mockApi({ 'GET /api/auth/session': signedIn, 'GET /api/settings/options': json(500, {}) })
    render(<App />)
    await screen.findByText('What can I help with?')

    expect(screen.queryByRole('button', { name: 'Settings' })).not.toBeInTheDocument()
  })
})

describe('Settings helpers', () => {
  it('validates prices against the server maximum', () => {
    expect(priceError('0.11', 2)).toBeNull()
    expect(priceError('2', 2)).toBeNull()
    expect(priceError('0', 2)).not.toBeNull()
    expect(priceError('2.01', 2)).not.toBeNull()
    expect(priceError('', 2)).not.toBeNull()
    expect(priceError('abc', 2)).not.toBeNull()
  })

  it('treats 0.5 and 0.50 as the same price', () => {
    const values = { ...defaultValues(OPTIONS), peakPrice: '0.480' }

    expect(toRequest(values, OPTIONS)).toBeUndefined()
  })
})
