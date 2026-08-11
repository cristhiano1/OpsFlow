import { render, screen, waitFor, act } from '@testing-library/react'
import { StrictMode, useState } from 'react'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import type { LoginUserResponse } from './contracts'

vi.mock('./singleFlightRefresh', () => ({
  refreshOnce: vi.fn(),
}))

vi.mock('./sessionEpoch', () => ({
  SESSION_EPOCH_STORAGE_KEY: 'opsflow.auth.session-epoch',
  readEpoch: vi.fn(),
}))

vi.mock('./sessionStore', () => ({
  currentGeneration: vi.fn(),
  invalidateSession: vi.fn(),
  setSession: vi.fn(),
  subscribeSessionInvalidation: vi.fn(),
}))

vi.mock('./authApi', () => ({
  login: vi.fn(),
  logout: vi.fn(),
}))

import { AuthProvider } from './AuthProvider'
import type { LoginResult } from './authContext'
import { useAuth } from './useAuth'
import { refreshOnce } from './singleFlightRefresh'
import { readEpoch } from './sessionEpoch'
import { currentGeneration, invalidateSession, setSession, subscribeSessionInvalidation } from './sessionStore'
import { login as authLogin, logout as authLogout } from './authApi'

const USER_A: LoginUserResponse = {
  userId: 'u1',
  email: 'alice@example.com',
  displayName: 'Alice',
  organizationId: 'org1',
  organizationName: 'Acme',
  roles: ['admin'],
}

const USER_B: LoginUserResponse = {
  userId: 'u2',
  email: 'bob@example.com',
  displayName: 'Bob',
  organizationId: 'org2',
  organizationName: 'Globex',
  roles: ['member'],
}

function refreshedResult(user: LoginUserResponse, epoch: string) {
  return {
    kind: 'refreshed' as const,
    data: { accessToken: `tok-${epoch}`, accessTokenExpiresAt: '2099-01-01T00:00:00Z', user },
    sessionEpoch: epoch,
  }
}

function loginSuccess(user: LoginUserResponse, epoch: string) {
  return {
    kind: 'success' as const,
    data: { accessToken: `tok-${epoch}`, accessTokenExpiresAt: '2099-01-01T00:00:00Z', user },
    epoch,
  }
}

function TestConsumer() {
  const { state, login, logout, retryBootstrap } = useAuth()
  const [loginResult, setLoginResult] = useState<LoginResult | null>(null)

  return (
    <div>
      <div data-testid="status">{state.status}</div>
      {state.status === 'authenticated' && (
        <div data-testid="user">{state.user.displayName}</div>
      )}
      {state.status === 'unauthenticated' && state.notice && (
        <div data-testid="notice">{state.notice.message}</div>
      )}
      {loginResult && <div data-testid="login-result">{loginResult.kind}</div>}
      <button
        data-testid="login-btn"
        onClick={async () => setLoginResult(await login('a@b.com', 'pw'))}
      >
        Login
      </button>
      <button data-testid="logout-btn" onClick={logout}>
        Logout
      </button>
      <button data-testid="retry-btn" onClick={retryBootstrap}>
        Retry
      </button>
    </div>
  )
}

function renderProvider() {
  return render(
    <AuthProvider>
      <TestConsumer />
    </AuthProvider>,
  )
}

let capturedInvalidationListener: (() => void) | null = null

beforeEach(() => {
  vi.resetAllMocks()
  capturedInvalidationListener = null
  vi.mocked(subscribeSessionInvalidation).mockImplementation((listener) => {
    capturedInvalidationListener = listener
    return vi.fn()
  })
  vi.mocked(readEpoch).mockReturnValue({ status: 'present', epoch: 'E1' })
  vi.mocked(refreshOnce).mockResolvedValue({ kind: 'unauthenticated' })
  vi.mocked(currentGeneration).mockReturnValue(0)
  vi.mocked(setSession).mockReturnValue(true)
  vi.mocked(invalidateSession).mockReturnValue(1)
})

// ── Bootstrap ──────────────────────────────────────────────

describe('bootstrap', () => {
  it('resolves to authenticated when refreshOnce returns refreshed', async () => {
    vi.mocked(refreshOnce).mockResolvedValueOnce(refreshedResult(USER_A, 'E1'))

    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated')
    })
    expect(screen.getByTestId('user')).toHaveTextContent('Alice')
    expect(vi.mocked(refreshOnce)).toHaveBeenCalledWith(
      { kind: 'present', epoch: 'E1' },
      null,
    )
  })

  it('resolves to unauthenticated when refreshOnce returns unauthenticated', async () => {
    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    })
  })

  it('resolves to unavailable when epoch storage is unreadable', async () => {
    vi.mocked(readEpoch).mockReturnValue({
      status: 'unavailable',
      error: new Error('no storage'),
    })

    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unavailable')
    })
    expect(vi.mocked(refreshOnce)).not.toHaveBeenCalled()
  })

  it('resolves to unavailable when refreshOnce returns unavailable', async () => {
    vi.mocked(refreshOnce).mockResolvedValueOnce({
      kind: 'unavailable',
      error: new Error('lock'),
    })

    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unavailable')
    })
  })

  it('does not duplicate bootstrap in StrictMode', async () => {
    vi.mocked(refreshOnce).mockResolvedValue({ kind: 'unauthenticated' })

    render(
      <StrictMode>
        <AuthProvider>
          <TestConsumer />
        </AuthProvider>
      </StrictMode>,
    )

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    })
    expect(vi.mocked(refreshOnce)).toHaveBeenCalledTimes(1)
  })

  it('retryBootstrap re-runs bootstrap from unavailable', async () => {
    vi.mocked(refreshOnce).mockResolvedValueOnce({
      kind: 'unavailable',
      error: new Error('x'),
    })

    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unavailable')
    })

    vi.mocked(refreshOnce).mockResolvedValueOnce(refreshedResult(USER_A, 'E1'))

    await act(async () => {
      screen.getByTestId('retry-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated')
    })
  })

  it('bootstraps with existing present epoch (reload scenario)', async () => {
    vi.mocked(readEpoch).mockReturnValue({ status: 'present', epoch: 'E-EXISTING' })
    vi.mocked(refreshOnce).mockResolvedValueOnce(
      refreshedResult(USER_A, 'E-EXISTING'),
    )

    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated')
    })
    expect(vi.mocked(refreshOnce)).toHaveBeenCalledWith(
      { kind: 'present', epoch: 'E-EXISTING' },
      null,
    )
  })

  it('bootstraps with missing epoch', async () => {
    vi.mocked(readEpoch).mockReturnValue({ status: 'missing' })
    vi.mocked(refreshOnce).mockResolvedValueOnce({ kind: 'unauthenticated' })

    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    })
    expect(vi.mocked(refreshOnce)).toHaveBeenCalledWith(
      { kind: 'missing' },
      null,
    )
  })
})

// ── Bootstrap session-replaced retry ───────────────────────

describe('bootstrap session-replaced retry', () => {
  it('retries once on PRESENT epoch replacement and succeeds', async () => {
    vi.mocked(readEpoch)
      .mockReturnValueOnce({ status: 'present', epoch: 'E1' }) // initial
      .mockReturnValue({ status: 'present', epoch: 'E2' }) // after replacement

    vi.mocked(refreshOnce)
      .mockResolvedValueOnce({ kind: 'session-replaced' }) // first attempt
      .mockResolvedValueOnce(refreshedResult(USER_B, 'E2')) // retry

    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated')
    })
    expect(screen.getByTestId('user')).toHaveTextContent('Bob')
    expect(vi.mocked(refreshOnce)).toHaveBeenCalledTimes(2)
    expect(vi.mocked(refreshOnce)).toHaveBeenNthCalledWith(
      2,
      { kind: 'present', epoch: 'E2' },
      null,
    )
  })

  it('retries once on MISSING to PRESENT replacement and succeeds', async () => {
    vi.mocked(readEpoch)
      .mockReturnValueOnce({ status: 'missing' }) // initial
      .mockReturnValue({ status: 'present', epoch: 'E1' }) // sibling created

    vi.mocked(refreshOnce)
      .mockResolvedValueOnce({ kind: 'session-replaced' })
      .mockResolvedValueOnce(refreshedResult(USER_A, 'E1'))

    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated')
    })
    expect(vi.mocked(refreshOnce)).toHaveBeenCalledTimes(2)
    expect(vi.mocked(refreshOnce)).toHaveBeenNthCalledWith(
      2,
      { kind: 'present', epoch: 'E1' },
      null,
    )
  })

  it('fails to unavailable after exhausting retries', async () => {
    vi.mocked(refreshOnce).mockResolvedValue({ kind: 'session-replaced' })

    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unavailable')
    })
    expect(vi.mocked(refreshOnce)).toHaveBeenCalledTimes(2) // initial + 1 retry
  })
})

// ── Login ──────────────────────────────────────────────────

describe('login', () => {
  async function bootstrapToUnauthenticated() {
    renderProvider()
    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    })
  }

  it('installs session when both epoch checks pass', async () => {
    await bootstrapToUnauthenticated()

    vi.mocked(authLogin).mockResolvedValue(loginSuccess(USER_A, 'E1'))
    vi.mocked(readEpoch).mockReturnValue({ status: 'present', epoch: 'E1' })

    await act(async () => {
      screen.getByTestId('login-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated')
    })
    expect(screen.getByTestId('user')).toHaveTextContent('Alice')
    expect(screen.getByTestId('login-result')).toHaveTextContent('success')
    expect(vi.mocked(setSession)).toHaveBeenCalledWith(
      'tok-E1',
      { userId: 'u1', organizationId: 'org1' },
      'E1',
      0,
    )
  })

  it('rejects stale generation (setSession returns false)', async () => {
    await bootstrapToUnauthenticated()

    vi.mocked(authLogin).mockResolvedValue(loginSuccess(USER_A, 'E1'))
    vi.mocked(readEpoch).mockReturnValue({ status: 'present', epoch: 'E1' })
    vi.mocked(setSession).mockReturnValue(false)
    vi.mocked(refreshOnce).mockResolvedValueOnce({ kind: 'unauthenticated' })

    await act(async () => {
      screen.getByTestId('login-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('login-result')).toHaveTextContent('replaced')
    })
  })

  it('rejects pre-install epoch mismatch', async () => {
    await bootstrapToUnauthenticated()

    vi.mocked(authLogin).mockResolvedValue(loginSuccess(USER_A, 'E1'))
    vi.mocked(readEpoch).mockReturnValue({ status: 'present', epoch: 'E2' })
    vi.mocked(refreshOnce).mockResolvedValueOnce({ kind: 'unauthenticated' })

    await act(async () => {
      screen.getByTestId('login-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('login-result')).toHaveTextContent('replaced')
    })
    expect(vi.mocked(setSession)).not.toHaveBeenCalled()
  })

  it('rejects pre-install epoch missing', async () => {
    await bootstrapToUnauthenticated()

    vi.mocked(authLogin).mockResolvedValue(loginSuccess(USER_A, 'E1'))
    vi.mocked(readEpoch).mockReturnValue({ status: 'missing' })
    vi.mocked(refreshOnce).mockResolvedValueOnce({ kind: 'unauthenticated' })

    await act(async () => {
      screen.getByTestId('login-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('login-result')).toHaveTextContent('replaced')
    })
    expect(vi.mocked(setSession)).not.toHaveBeenCalled()
  })

  it('returns unavailable when pre-install readEpoch is unavailable', async () => {
    await bootstrapToUnauthenticated()

    vi.mocked(authLogin).mockResolvedValue(loginSuccess(USER_A, 'E1'))
    vi.mocked(readEpoch).mockReturnValue({
      status: 'unavailable',
      error: new Error('gone'),
    })

    await act(async () => {
      screen.getByTestId('login-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unavailable')
    })
    expect(screen.getByTestId('login-result')).toHaveTextContent('unavailable')
    expect(vi.mocked(setSession)).not.toHaveBeenCalled()
  })

  it('invalidates and re-bootstraps when post-install epoch changes', async () => {
    await bootstrapToUnauthenticated()

    vi.mocked(authLogin).mockResolvedValue(loginSuccess(USER_A, 'E1'))
    let readCount = 0
    vi.mocked(readEpoch).mockImplementation(() => {
      readCount++
      // First call (pre-install): E1. Second call (post-install): E2.
      if (readCount <= 1) return { status: 'present', epoch: 'E1' }
      return { status: 'present', epoch: 'E2' }
    })
    vi.mocked(refreshOnce).mockResolvedValueOnce({ kind: 'unauthenticated' })

    await act(async () => {
      screen.getByTestId('login-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('login-result')).toHaveTextContent('replaced')
    })
    expect(vi.mocked(invalidateSession)).toHaveBeenCalled()
  })

  it('invalidates and returns unavailable when post-install readEpoch is unavailable', async () => {
    await bootstrapToUnauthenticated()

    vi.mocked(authLogin).mockResolvedValue(loginSuccess(USER_A, 'E1'))
    let readCount = 0
    vi.mocked(readEpoch).mockImplementation(() => {
      readCount++
      if (readCount <= 1) return { status: 'present', epoch: 'E1' }
      return { status: 'unavailable', error: new Error('storage gone') }
    })

    await act(async () => {
      screen.getByTestId('login-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unavailable')
    })
    expect(screen.getByTestId('login-result')).toHaveTextContent('unavailable')
    expect(vi.mocked(invalidateSession)).toHaveBeenCalled()
  })

  it('returns invalid-credentials on authApi failure', async () => {
    await bootstrapToUnauthenticated()

    vi.mocked(authLogin).mockResolvedValue({ kind: 'invalid-credentials' })

    await act(async () => {
      screen.getByTestId('login-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('login-result')).toHaveTextContent(
        'invalid-credentials',
      )
    })
    expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
  })

  it('returns unavailable on authApi unavailable', async () => {
    await bootstrapToUnauthenticated()

    vi.mocked(authLogin).mockResolvedValue({
      kind: 'unavailable',
      error: new Error('net'),
    })

    await act(async () => {
      screen.getByTestId('login-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('login-result')).toHaveTextContent(
        'unavailable',
      )
    })
    expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
  })
})

// ── Logout ─────────────────────────────────────────────────

describe('logout', () => {
  async function bootstrapToAuthenticated() {
    vi.mocked(refreshOnce).mockResolvedValueOnce(refreshedResult(USER_A, 'E1'))
    renderProvider()
    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated')
    })
  }

  it('transitions to unauthenticated on confirmed logout', async () => {
    await bootstrapToAuthenticated()

    vi.mocked(authLogout).mockResolvedValue({ kind: 'success' })

    await act(async () => {
      screen.getByTestId('logout-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    })
    expect(vi.mocked(invalidateSession)).toHaveBeenCalled()
    expect(screen.queryByTestId('notice')).toBeNull()
  })

  it('transitions to unauthenticated with notice on ambiguous logout', async () => {
    await bootstrapToAuthenticated()

    vi.mocked(authLogout).mockResolvedValue({
      kind: 'unavailable',
      error: new Error('net'),
    })

    await act(async () => {
      screen.getByTestId('logout-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    })
    expect(vi.mocked(invalidateSession)).toHaveBeenCalled()
    expect(screen.getByTestId('notice')).toHaveTextContent(
      'Logout could not be confirmed',
    )
  })
})

// ── Cross-tab storage events ───────────────────────────────

describe('cross-tab storage events', () => {
  async function bootstrapToAuthenticated() {
    vi.mocked(refreshOnce).mockResolvedValueOnce(refreshedResult(USER_A, 'E1'))
    renderProvider()
    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated')
    })
  }

  function dispatchEpochChange(newValue: string) {
    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'opsflow.auth.session-epoch',
        newValue,
      }),
    )
  }

  it('hides authenticated UI immediately on epoch change', async () => {
    await bootstrapToAuthenticated()

    let resolveReBootstrap!: (v: { kind: 'unauthenticated' }) => void
    vi.mocked(readEpoch).mockReturnValue({ status: 'present', epoch: 'E2' })
    vi.mocked(refreshOnce).mockReturnValueOnce(
      new Promise((resolve) => {
        resolveReBootstrap = resolve
      }),
    )

    act(() => {
      dispatchEpochChange('E2')
    })

    expect(screen.getByTestId('status')).toHaveTextContent('loading')
    expect(vi.mocked(invalidateSession)).toHaveBeenCalled()

    await act(async () => {
      resolveReBootstrap({ kind: 'unauthenticated' })
    })

    expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
  })

  it('re-bootstraps to authenticated with new user', async () => {
    await bootstrapToAuthenticated()

    vi.mocked(readEpoch).mockReturnValue({ status: 'present', epoch: 'E2' })
    vi.mocked(refreshOnce).mockResolvedValueOnce(refreshedResult(USER_B, 'E2'))

    await act(async () => {
      dispatchEpochChange('E2')
    })

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated')
    })
    expect(screen.getByTestId('user')).toHaveTextContent('Bob')
  })

  it('re-bootstraps to unauthenticated on sibling logout', async () => {
    await bootstrapToAuthenticated()

    vi.mocked(readEpoch).mockReturnValue({ status: 'present', epoch: 'E2' })
    vi.mocked(refreshOnce).mockResolvedValueOnce({ kind: 'unauthenticated' })

    await act(async () => {
      dispatchEpochChange('E2')
    })

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    })
  })

  it('re-bootstraps to unavailable', async () => {
    await bootstrapToAuthenticated()

    vi.mocked(readEpoch).mockReturnValue({ status: 'present', epoch: 'E2' })
    vi.mocked(refreshOnce).mockResolvedValueOnce({
      kind: 'unavailable',
      error: new Error('x'),
    })

    await act(async () => {
      dispatchEpochChange('E2')
    })

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unavailable')
    })
  })

  it('ignores storage events for unrelated keys', async () => {
    await bootstrapToAuthenticated()

    act(() => {
      window.dispatchEvent(
        new StorageEvent('storage', {
          key: 'some-other-key',
          newValue: 'whatever',
        }),
      )
    })

    expect(screen.getByTestId('status')).toHaveTextContent('authenticated')
    expect(vi.mocked(invalidateSession)).not.toHaveBeenCalled()
  })
})

// ── P1: Local session invalidation notification ───────────

describe('local session invalidation (P1)', () => {
  it('clears authenticated state on external session invalidation', async () => {
    vi.mocked(refreshOnce).mockResolvedValueOnce(refreshedResult(USER_A, 'E1'))
    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated')
    })

    act(() => {
      capturedInvalidationListener!()
    })

    expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
  })

  it('ignores invalidation when state is not authenticated', async () => {
    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    })

    act(() => {
      capturedInvalidationListener!()
    })

    expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
  })

  it('removes invalidation listener on unmount', async () => {
    const unsubscribe = vi.fn()
    vi.mocked(subscribeSessionInvalidation).mockReturnValue(unsubscribe)

    const { unmount } = renderProvider()
    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    })

    unmount()
    expect(unsubscribe).toHaveBeenCalledTimes(1)
  })
})

// ── P2: Logout dominates outstanding bootstraps ───────────

describe('logout dominates bootstraps (P2)', () => {
  function dispatchEpochChange(newValue: string) {
    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'opsflow.auth.session-epoch',
        newValue,
      }),
    )
  }

  async function bootstrapToAuthenticated() {
    vi.mocked(refreshOnce).mockResolvedValueOnce(refreshedResult(USER_A, 'E1'))
    renderProvider()
    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated')
    })
  }

  it('logout success dominates concurrent bootstrap from storage event', async () => {
    await bootstrapToAuthenticated()

    let resolveStorageBootstrap!: (v: { kind: 'refreshed'; data: { accessToken: string; accessTokenExpiresAt: string; user: LoginUserResponse }; sessionEpoch: string }) => void
    vi.mocked(readEpoch).mockReturnValue({ status: 'present', epoch: 'E2' })
    vi.mocked(refreshOnce).mockReturnValueOnce(
      new Promise((resolve) => { resolveStorageBootstrap = resolve }),
    )

    act(() => {
      dispatchEpochChange('E2')
    })

    expect(screen.getByTestId('status')).toHaveTextContent('loading')

    vi.mocked(authLogout).mockResolvedValue({ kind: 'success' })

    await act(async () => {
      screen.getByTestId('logout-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    })

    await act(async () => {
      resolveStorageBootstrap(refreshedResult(USER_B, 'E2'))
    })

    expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    expect(screen.queryByTestId('notice')).toBeNull()
  })

  it('logout unavailable dominates concurrent bootstrap from storage event', async () => {
    await bootstrapToAuthenticated()

    let resolveStorageBootstrap!: (v: { kind: 'refreshed'; data: { accessToken: string; accessTokenExpiresAt: string; user: LoginUserResponse }; sessionEpoch: string }) => void
    vi.mocked(readEpoch).mockReturnValue({ status: 'present', epoch: 'E2' })
    vi.mocked(refreshOnce).mockReturnValueOnce(
      new Promise((resolve) => { resolveStorageBootstrap = resolve }),
    )

    act(() => {
      dispatchEpochChange('E2')
    })

    vi.mocked(authLogout).mockResolvedValue({
      kind: 'unavailable',
      error: new Error('net'),
    })

    await act(async () => {
      screen.getByTestId('logout-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    })
    expect(screen.getByTestId('notice')).toHaveTextContent(
      'Logout could not be confirmed',
    )

    await act(async () => {
      resolveStorageBootstrap(refreshedResult(USER_B, 'E2'))
    })

    expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    expect(screen.getByTestId('notice')).toHaveTextContent(
      'Logout could not be confirmed',
    )
  })

  it('storage event during logout does not start new bootstrap', async () => {
    await bootstrapToAuthenticated()

    let resolveLogout!: (v: { kind: 'success' }) => void
    vi.mocked(authLogout).mockReturnValue(
      new Promise((resolve) => { resolveLogout = resolve }),
    )

    act(() => {
      screen.getByTestId('logout-btn').click()
    })

    vi.mocked(refreshOnce).mockClear()

    act(() => {
      dispatchEpochChange('E3')
    })

    expect(vi.mocked(refreshOnce)).not.toHaveBeenCalled()

    await act(async () => {
      resolveLogout({ kind: 'success' })
    })

    expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
  })

  it('no hidden bootstrap fires after logout settles', async () => {
    await bootstrapToAuthenticated()

    vi.mocked(authLogout).mockResolvedValue({ kind: 'success' })

    await act(async () => {
      screen.getByTestId('logout-btn').click()
    })

    expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')

    const callsAfterLogout = vi.mocked(refreshOnce).mock.calls.length

    await act(async () => {})

    expect(vi.mocked(refreshOnce).mock.calls.length).toBe(callsAfterLogout)
    expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
  })

  it('unexpected authLogout rejection treated as ambiguous logout', async () => {
    await bootstrapToAuthenticated()

    vi.mocked(authLogout).mockRejectedValue(new Error('unexpected runtime error'))

    await act(async () => {
      screen.getByTestId('logout-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    })
    expect(vi.mocked(invalidateSession)).toHaveBeenCalled()
    expect(screen.getByTestId('notice')).toHaveTextContent(
      'Logout could not be confirmed',
    )
  })

  it('barrier releases after unexpected authLogout rejection so bootstrap can run', async () => {
    await bootstrapToAuthenticated()

    vi.mocked(authLogout).mockRejectedValue(new Error('unexpected runtime error'))

    await act(async () => {
      screen.getByTestId('logout-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    })

    vi.mocked(refreshOnce).mockResolvedValueOnce(refreshedResult(USER_B, 'E2'))

    await act(async () => {
      screen.getByTestId('retry-btn').click()
    })

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('authenticated')
    })
    expect(screen.getByTestId('user')).toHaveTextContent('Bob')
  })
})
