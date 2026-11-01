import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from '../App'
import { json, mockApi, networkDown, signedIn, signedOut } from './mockApi'

describe('Invite screen', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  it('shows the invite screen when there is no session', async () => {
    mockApi({ 'GET /api/auth/session': signedOut })
    render(<App />)

    expect(await screen.findByLabelText('Invite code')).toBeInTheDocument()
    expect(screen.getByText('Please avoid entering personal information.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Continue' })).toBeDisabled()
  })

  it('goes straight to chat for a returning visitor', async () => {
    mockApi({ 'GET /api/auth/session': signedIn })
    render(<App />)

    expect(await screen.findByRole('button', { name: 'Should I charge my battery from the grid tonight?' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Invite code')).not.toBeInTheDocument()
  })

  it('opens chat after a correct code', async () => {
    const api = mockApi({ 'GET /api/auth/session': signedOut, 'POST /api/auth/redeem': signedIn })
    const user = userEvent.setup()
    render(<App />)

    await user.type(await screen.findByLabelText('Invite code'), '  abc123  ')
    await user.click(screen.getByRole('button', { name: 'Continue' }))

    expect(await screen.findByText('What can I help with?')).toBeInTheDocument()
    expect(api.calls.find((c) => c.path === '/api/auth/redeem')?.body).toEqual({ code: 'abc123' })
  })

  it('clears and focuses the code box after a wrong code', async () => {
    mockApi({ 'GET /api/auth/session': signedOut, 'POST /api/auth/redeem': json(401, { error: 'invalid_code' }) })
    const user = userEvent.setup()
    render(<App />)

    const input = await screen.findByLabelText('Invite code')
    await user.type(input, 'wrong')
    await user.click(screen.getByRole('button', { name: 'Continue' }))

    expect(await screen.findByText("That code didn't work. Check it and try again.")).toBeInTheDocument()
    expect(input).toHaveValue('')
    expect(input).toHaveFocus()
  })

  it('locks Continue for 60 seconds after too many tries', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    mockApi({
      'GET /api/auth/session': signedOut,
      'POST /api/auth/redeem': json(429, { error: 'rate_limited' }, { 'Retry-After': '60' }),
    })
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    render(<App />)

    await user.type(await screen.findByLabelText('Invite code'), 'guess')
    await user.click(screen.getByRole('button', { name: 'Continue' }))

    expect(await screen.findByText('Too many tries. Wait a minute and try again.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Continue' })).toBeDisabled()

    await act(async () => {
      vi.advanceTimersByTime(61_000)
    })

    expect(screen.getByRole('button', { name: 'Continue' })).toBeEnabled()
    expect(screen.queryByText('Too many tries. Wait a minute and try again.')).not.toBeInTheDocument()
  })

  it('says when the visitor is offline', async () => {
    mockApi({ 'GET /api/auth/session': signedOut, 'POST /api/auth/redeem': networkDown })
    vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false)
    const user = userEvent.setup()
    render(<App />)

    await user.type(await screen.findByLabelText('Invite code'), 'abc')
    await user.click(screen.getByRole('button', { name: 'Continue' }))

    expect(await screen.findByText('You appear to be offline.')).toBeInTheDocument()
  })
})
