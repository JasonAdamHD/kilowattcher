import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import App from '../App'
import { json, mockApi, networkDown, reply, signedIn, signedOut } from './mockApi'

const CHARGE = 'Should I charge my battery from the grid tonight?'

async function openChat() {
  const user = userEvent.setup()
  render(<App />)
  await screen.findByText('What can I help with?')
  return user
}

function messageBox() {
  return screen.getByLabelText('Your question')
}

describe('Chat screen', () => {
  it('shows three starter questions when empty', async () => {
    mockApi({ 'GET /api/auth/session': signedIn })
    await openChat()

    expect(screen.getAllByRole('button', { name: /\?$/ }).filter((b) => b.className === 'starter')).toHaveLength(3)
    expect(screen.getByRole('button', { name: 'What does reserve percentage mean?' })).toBeInTheDocument()
  })

  it('sends a starter question and shows the answer with tool chips', async () => {
    let finish!: () => void
    const answered = new Promise<void>((resolve) => (finish = resolve))
    mockApi({
      'GET /api/auth/session': signedIn,
      'POST /api/chat': async () => {
        await answered
        return reply({
          toolCalls: [
            { name: 'get_battery_status', arguments: {}, result: { stateOfChargePercent: 42 } },
            { name: 'get_rate_schedule', arguments: { date: '2026-11-01' }, result: { plan: 'Sunrise Saver' } },
            { name: 'get_solar_forecast', arguments: { date: '2026-11-02' }, result: { conditions: 'cloudy' } },
          ],
        })()
      },
    })
    const user = await openChat()

    await user.click(screen.getByRole('button', { name: CHARGE }))

    expect(screen.getByText(CHARGE, { selector: '.bubble--user' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Checking your battery and rates…')

    finish()
    expect(await screen.findByText(/Charge between midnight and 6 AM/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Battery: show the data used' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Rates: show the data used' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Solar forecast: show the data used' })).toBeInTheDocument()
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
  })

  it('tapping a tool chip shows and hides its raw result', async () => {
    mockApi({
      'GET /api/auth/session': signedIn,
      'POST /api/chat': reply({
        toolCalls: [{ name: 'get_solar_forecast', arguments: { date: '2026-11-02' }, result: { conditions: 'cloudy' } }],
      }),
    })
    const user = await openChat()
    await user.click(screen.getByRole('button', { name: CHARGE }))

    const chip = await screen.findByRole('button', { name: 'Solar forecast: show the data used' })
    await user.click(chip)

    expect(chip).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByText(/"conditions": "cloudy"/)).toBeInTheDocument()
    expect(screen.getByText(/get_solar_forecast\(\{"date":"2026-11-02"\}\)/)).toBeInTheDocument()

    await user.click(chip)
    expect(screen.queryByText(/"conditions": "cloudy"/)).not.toBeInTheDocument()
  })

  it('opens a citation in the overlay and returns focus on close', async () => {
    mockApi({
      'GET /api/auth/session': signedIn,
      'POST /api/chat': reply({
        answer: 'The reserve is the charge kept back for outages.',
        toolCalls: [{ name: 'search_docs', arguments: { query: 'reserve' }, result: { results: [] } }],
        citations: [
          {
            id: 'battery-manual#reserve-percentage',
            title: "VoltNest 13 Owner's Guide",
            heading: 'Reserve percentage',
            text: 'The reserve percentage is the share of charge kept back for outages.',
          },
        ],
      }),
    })
    const user = await openChat()
    await user.click(screen.getByRole('button', { name: 'What does reserve percentage mean?' }))

    const marker = await screen.findByRole('button', { name: "Source 1: VoltNest 13 Owner's Guide, Reserve percentage" })
    expect(screen.getByRole('button', { name: "VoltNest 13 Owner's Guide — Reserve percentage" })).toBeInTheDocument()
    await user.click(marker)

    const dialog = screen.getByRole('dialog', { name: 'Reserve percentage' })
    expect(within(dialog).getByText(/share of charge kept back for outages/)).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Close' })).toHaveFocus()

    await user.click(within(dialog).getByRole('button', { name: 'Close' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(marker).toHaveFocus()
  })

  it('closes the overlay with Escape', async () => {
    mockApi({
      'GET /api/auth/session': signedIn,
      'POST /api/chat': reply({ citations: [{ id: 'faq#x', title: 'FAQ', heading: 'X', text: 'Passage.' }] }),
    })
    const user = await openChat()
    await user.click(screen.getByRole('button', { name: CHARGE }))
    await user.click(await screen.findByRole('button', { name: 'Source 1: FAQ, X' }))

    await user.keyboard('{Escape}')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('keeps Send disabled for an empty message', async () => {
    mockApi({ 'GET /api/auth/session': signedIn })
    const user = await openChat()

    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled()
    await user.type(messageBox(), '   ')
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled()
  })

  it('turns the counter red and disables Send over 1,000 characters, keeping the text', async () => {
    mockApi({ 'GET /api/auth/session': signedIn })
    const user = await openChat()

    await user.click(messageBox())
    await user.paste('a'.repeat(1001))

    expect(screen.getByText('1001 / 1000')).toHaveClass('counter--over')
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled()
    expect(messageBox()).toHaveValue('a'.repeat(1001))
  })

  it('sends typed questions with Enter and includes earlier turns as history', async () => {
    const api = mockApi({
      'GET /api/auth/session': signedIn,
      'POST /api/chat': [reply({ answer: 'Overnight, 12 AM to 6 AM.' }), reply({ answer: 'Same tomorrow.' })],
    })
    const user = await openChat()

    await user.type(messageBox(), 'When is power cheapest?{Enter}')
    await screen.findByText('Overnight, 12 AM to 6 AM.')
    await user.type(messageBox(), 'And tomorrow?{Enter}')
    await screen.findByText('Same tomorrow.')

    const [first, second] = api.chatCalls()
    expect(first.body).toEqual({ message: 'When is power cheapest?', history: [] })
    expect(second.body).toEqual({
      message: 'And tomorrow?',
      history: [
        { role: 'user', content: 'When is power cheapest?' },
        { role: 'assistant', content: 'Overnight, 12 AM to 6 AM.' },
      ],
    })
    expect(messageBox()).toHaveValue('')
  })

  it('sends at most the last 10 messages as history', async () => {
    const answers = Array.from({ length: 7 }, (_, i) => reply({ answer: `Answer ${i}` }))
    const api = mockApi({ 'GET /api/auth/session': signedIn, 'POST /api/chat': answers })
    const user = await openChat()

    for (let i = 0; i < 7; i++) {
      await user.type(messageBox(), `Question ${i}{Enter}`)
      await screen.findByText(`Answer ${i}`)
    }

    const last = api.chatCalls().at(-1)!.body as { history: unknown[] }
    expect(last.history).toHaveLength(10)
  })

  it('shows the busy message with Retry when Azure is at capacity', async () => {
    const api = mockApi({
      'GET /api/auth/session': signedIn,
      'POST /api/chat': [json(429, { error: 'assistant_busy' }), reply()],
    })
    const user = await openChat()
    await user.click(screen.getByRole('button', { name: CHARGE }))

    expect(await screen.findByText('The assistant is busy. Try again in a minute.')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Retry' }))

    expect(await screen.findByText(/Charge between midnight/)).toBeInTheDocument()
    expect(api.chatCalls()).toHaveLength(2)
    expect(api.chatCalls()[1].body).toEqual({ message: CHARGE, history: [] })
    expect(screen.getAllByText(CHARGE, { selector: '.bubble--user' })).toHaveLength(1)
  })

  it('tells a fast visitor exactly how long to wait', async () => {
    mockApi({
      'GET /api/auth/session': signedIn,
      'POST /api/chat': json(429, { error: 'rate_limited' }, { 'Retry-After': '4' }),
    })
    const user = await openChat()
    await user.click(screen.getByRole('button', { name: CHARGE }))

    expect(await screen.findByText("You're asking quickly. Try again in 4 seconds.")).toBeInTheDocument()
  })

  it.each([
    ['timeout', json(504, { error: 'assistant_timeout' })],
    ['server error', json(502, { error: 'assistant_unavailable' })],
    ['proxy error page', () => new Response('<html>Bad gateway</html>', { status: 502 })],
  ])('shows "something went wrong" with Retry on a %s', async (_, response) => {
    mockApi({ 'GET /api/auth/session': signedIn, 'POST /api/chat': response })
    const user = await openChat()
    await user.click(screen.getByRole('button', { name: CHARGE }))

    expect(await screen.findByText('Something went wrong reaching the assistant.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
  })

  it('asks for a rephrase when the model reply was invalid twice, keeping the question', async () => {
    mockApi({ 'GET /api/auth/session': signedIn, 'POST /api/chat': json(502, { error: 'invalid_answer' }) })
    const user = await openChat()
    await user.type(messageBox(), 'Explain everything about my bill{Enter}')

    expect(await screen.findByText("I couldn't put that answer together. Try rephrasing.")).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument()
    expect(messageBox()).toHaveValue('Explain everything about my bill')
  })

  it('says when the visitor is offline, with Retry', async () => {
    mockApi({ 'GET /api/auth/session': signedIn, 'POST /api/chat': [networkDown, reply()] })
    const online = vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false)
    const user = await openChat()
    await user.click(screen.getByRole('button', { name: CHARGE }))

    expect(await screen.findByText('You appear to be offline.')).toBeInTheDocument()

    online.mockReturnValue(true)
    await user.click(screen.getByRole('button', { name: 'Retry' }))
    expect(await screen.findByText(/Charge between midnight/)).toBeInTheDocument()
  })

  it('sends an expired session back to Invite and resends the question after redeeming', async () => {
    const api = mockApi({
      'GET /api/auth/session': signedIn,
      'POST /api/chat': [reply({ answer: 'First answer.' }), json(401, { error: 'unauthorized' }), reply({ answer: 'Resent answer.' })],
      'POST /api/auth/redeem': signedIn,
    })
    const user = await openChat()
    await user.type(messageBox(), 'First question{Enter}')
    await screen.findByText('First answer.')

    await user.type(messageBox(), 'Second question{Enter}')

    expect(await screen.findByText('Your session expired. Enter the invite code again.')).toBeInTheDocument()
    await user.type(screen.getByLabelText('Invite code'), 'abc123')
    await user.click(screen.getByRole('button', { name: 'Continue' }))

    expect(await screen.findByText('Resent answer.')).toBeInTheDocument()
    expect(screen.getByText('First answer.')).toBeInTheDocument()
    expect(api.chatCalls().at(-1)!.body).toEqual({
      message: 'Second question',
      history: [
        { role: 'user', content: 'First question' },
        { role: 'assistant', content: 'First answer.' },
      ],
    })
    expect(screen.getAllByText('Second question', { selector: '.bubble--user' })).toHaveLength(1)
  })

  it('offers the suggested question after an off-topic decline', async () => {
    const api = mockApi({
      'GET /api/auth/session': signedIn,
      'POST /api/chat': [
        reply({ answer: 'I can only help with home energy.', suggestedQuestion: 'When is electricity cheapest tomorrow?' }),
        reply({ answer: '12 AM to 6 AM at $0.11 per kWh.' }),
      ],
    })
    const user = await openChat()
    await user.type(messageBox(), 'Write me a poem about cats.{Enter}')

    await user.click(await screen.findByRole('button', { name: 'Try: When is electricity cheapest tomorrow?' }))

    expect(await screen.findByText('12 AM to 6 AM at $0.11 per kWh.')).toBeInTheDocument()
    expect(api.chatCalls()[1].body).toMatchObject({ message: 'When is electricity cheapest tomorrow?' })
  })

  it('shows the round-limit note that comes back in the answer', async () => {
    mockApi({
      'GET /api/auth/session': signedIn,
      'POST /api/chat': reply({ answer: 'Your battery is at 42%.\n\nI may have missed something; try a narrower question.' }),
    })
    const user = await openChat()
    await user.click(screen.getByRole('button', { name: CHARGE }))

    expect(await screen.findByText('I may have missed something; try a narrower question.')).toBeInTheDocument()
  })

  it('New chat clears the conversation', async () => {
    mockApi({ 'GET /api/auth/session': signedIn, 'POST /api/chat': reply() })
    const user = await openChat()
    await user.click(screen.getByRole('button', { name: CHARGE }))
    await screen.findByText(/Charge between midnight/)

    await user.click(screen.getByRole('button', { name: 'New chat' }))

    expect(screen.queryByText(/Charge between midnight/)).not.toBeInTheDocument()
    expect(screen.getByText('What can I help with?')).toBeInTheDocument()
  })

  it('announces new messages to screen readers', async () => {
    mockApi({ 'GET /api/auth/session': signedIn })
    await openChat()

    expect(screen.getByLabelText('Conversation')).toHaveAttribute('aria-live', 'polite')
  })
})

describe('Session check', () => {
  it('falls back to Invite when the session check fails', async () => {
    mockApi({ 'GET /api/auth/session': signedOut })
    render(<App />)

    expect(await screen.findByLabelText('Invite code')).toBeInTheDocument()
  })
})
