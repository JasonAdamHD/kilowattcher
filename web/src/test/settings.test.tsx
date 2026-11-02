import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import App from '../App'
import type { Place, SettingsOptions } from '../api'
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

const AUSTIN: Place = { name: 'Austin, Texas', latitude: 30.2672, longitude: -97.7431, timeZone: 'America/Chicago', signature: 'sig-austin' }

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
    expect(screen.getByRole('button', { name: 'Phoenix, AZ' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByText('Phoenix, AZ', { selector: 'strong' })).toBeInTheDocument()
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
    await user.click(screen.getByRole('button', { name: 'Seattle, WA' }))
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

  it('finds a place by ZIP code and sends it with the next question', async () => {
    const { api, user } = await openChat({
      'GET /api/locations/search?q=78701': json(200, { results: [AUSTIN] }),
    })
    await user.click(screen.getByRole('button', { name: 'Settings' }))

    await user.type(screen.getByRole('combobox', { name: 'Home location' }), '78701')
    const option = await screen.findByRole('option', { name: 'Austin, Texas' })
    await user.click(option)

    expect(screen.getByText('Austin, Texas', { selector: 'strong' })).toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Home location' })).toHaveValue('')
    expect(screen.getByRole('button', { name: 'Phoenix, AZ' })).toHaveAttribute('aria-pressed', 'false')

    await user.type(screen.getByLabelText('Your question'), 'Charge tonight?{Enter}')
    await screen.findByText(/Charge between midnight/)
    expect(api.chatCalls()[0].body).toEqual({ message: 'Charge tonight?', history: [], settings: { location: AUSTIN } })
  })

  it('picks a match with the arrow keys and Enter, and a preset city replaces it', async () => {
    const { api, user } = await openChat({
      'GET /api/locations/search?q=Austin': json(200, { results: [AUSTIN] }),
    })
    await user.click(screen.getByRole('button', { name: 'Settings' }))
    const box = screen.getByRole('combobox', { name: 'Home location' })

    await user.type(box, 'Austin')
    await screen.findByRole('option', { name: 'Austin, Texas' })
    await user.keyboard('{ArrowDown}')
    expect(box).toHaveAttribute('aria-activedescendant')
    await user.keyboard('{Enter}')
    expect(screen.getByText('Austin, Texas', { selector: 'strong' })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Seattle, WA' }))
    expect(screen.getByText('Seattle, WA', { selector: 'strong' })).toBeInTheDocument()

    await user.type(screen.getByLabelText('Your question'), 'hi{Enter}')
    await screen.findByText(/Charge between midnight/)
    expect(api.chatCalls()[0].body).toEqual({ message: 'hi', history: [], settings: { city: 'seattle' } })
  })

  it('says when nothing matches, and Escape clears the search before closing the panel', async () => {
    const { user } = await openChat({
      'GET /api/locations/search?q=Zzyzxq': json(200, { results: [] }),
    })
    await user.click(screen.getByRole('button', { name: 'Settings' }))
    const box = screen.getByRole('combobox', { name: 'Home location' })

    await user.type(box, 'Zzyzxq')
    expect(await screen.findByText(/No US places match/)).toBeInTheDocument()

    await user.keyboard('{Escape}')
    expect(box).toHaveValue('')
    expect(panel()).toBeInTheDocument()

    await user.keyboard('{Escape}')
    expect(panel()).not.toBeInTheDocument()
  })

  it('explains when search is unavailable', async () => {
    const { user } = await openChat({
      'GET /api/locations/search?q=Austin': json(502, { error: 'location_search_unavailable' }),
    })
    await user.click(screen.getByRole('button', { name: 'Settings' }))

    await user.type(screen.getByRole('combobox', { name: 'Home location' }), 'Austin')

    expect(await screen.findByText(/Search isn't available right now/)).toBeInTheDocument()
  })

  it('remembers a searched place between visits', async () => {
    localStorage.setItem('kilowattcher.settings.v1', JSON.stringify({ location: AUSTIN }))
    const { user } = await openChat()
    await user.click(screen.getByRole('button', { name: 'Settings' }))

    expect(screen.getByText('Austin, Texas', { selector: 'strong' })).toBeInTheDocument()
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
