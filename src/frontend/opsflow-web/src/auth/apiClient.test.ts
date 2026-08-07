import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { apiFetch, ApiUnavailableError, SessionReplacedError, type ApiRequest } from './apiClient'
import {
  AUTH_LOGIN_PATH,
  AUTH_LOGOUT_PATH,
  AUTH_ME_PATH,
  AUTH_REFRESH_PATH,
} from './authApi'
import type { LoginResponse } from './contracts'
import { SESSION_EPOCH_STORAGE_KEY, _resetSessionEpochForTests } from './sessionEpoch'
import {
  _resetSessionStoreForTests,
  currentGeneration,
  getAccessToken,
  getBoundEpoch,
  getPrincipal,
  type Principal,
  setSession,
} from './sessionStore'
import { _resetSingleFlightForTests } from './singleFlightRefresh'

// ────────────────────────────────────────────────────────────────────────
// Fixtures
// ────────────────────────────────────────────────────────────────────────

const EPOCH_A = 'epoch-A'
const EPOCH_B = 'epoch-B'

const PRINCIPAL_A: Principal = {
  userId: '11111111-1111-1111-1111-111111111111',
  organizationId: '22222222-2222-2222-2222-222222222222',
}
const PRINCIPAL_B: Principal = {
  userId: '99999999-9999-9999-9999-999999999999',
  organizationId: '88888888-8888-8888-8888-888888888888',
}

interface Deferred<T> {
  promise: Promise<T>
  resolve: (value: T) => void
  reject: (error: unknown) => void
}

function defer<T>(): Deferred<T> {
  let resolve!: (value: T) => void
  let reject!: (error: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

function jsonBody(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function sampleLoginAs(token: string, p: Principal): LoginResponse {
  return {
    accessToken: token,
    accessTokenExpiresAt: '2030-01-01T00:00:00.000+00:00',
    user: {
      userId: p.userId,
      email: 'user@test.local',
      displayName: 'Test User',
      organizationId: p.organizationId,
      organizationName: 'Test Org',
      roles: ['Coordinator'],
    },
  }
}

function sampleLogin(token: string): LoginResponse {
  return sampleLoginAs(token, PRINCIPAL_A)
}

interface FetchCall {
  input: RequestInfo | URL
  init: RequestInit | undefined
}

interface FetchHarness {
  calls: FetchCall[]
  queue: Array<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>
}

function stubFetch(): FetchHarness {
  const harness: FetchHarness = { calls: [], queue: [] }
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      harness.calls.push({ input, init })
      const handler = harness.queue.shift()
      if (handler === undefined) {
        throw new Error(`fetch called with no queued handler for ${String(input)}`)
      }
      return handler(input, init)
    }),
  )
  return harness
}

// Passthrough fake for `navigator.locks` — refresh can run immediately.
function installPassthroughLocks(): void {
  Object.defineProperty(navigator, 'locks', {
    configurable: true,
    value: {
      request: async (_name: string, ...args: unknown[]) => {
        const callback = (typeof args[0] === 'function' ? args[0] : args[1]) as (
          lock: unknown,
        ) => Promise<unknown>
        return await callback({})
      },
    },
  })
}

// Gated fake lock: exposes `requested` (resolves when request() is invoked)
// and `release` (lets the callback run). Enables deterministic "epoch changed
// while WAITING for the lock" tests with no timers.
interface GatedLock {
  requested: Promise<void>
  release: () => void
}
function installGatedLocks(): GatedLock {
  const requested = defer<void>()
  const gate = defer<void>()
  Object.defineProperty(navigator, 'locks', {
    configurable: true,
    value: {
      request: async (_name: string, ...args: unknown[]) => {
        const callback = (typeof args[0] === 'function' ? args[0] : args[1]) as (
          lock: unknown,
        ) => Promise<unknown>
        requested.resolve()
        await gate.promise
        return await callback({})
      },
    },
  })
  return { requested: requested.promise, release: () => gate.resolve() }
}

function uninstallLocks(): void {
  Object.defineProperty(navigator, 'locks', { configurable: true, value: undefined })
}

beforeEach(() => {
  _resetSessionStoreForTests()
  _resetSingleFlightForTests()
  _resetSessionEpochForTests()
  localStorage.clear()
  localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_A)
  installPassthroughLocks()
})

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  uninstallLocks()
  localStorage.clear()
})

// ────────────────────────────────────────────────────────────────────────
// Bearer injection
// ────────────────────────────────────────────────────────────────────────

describe('apiClient bearer injection', () => {
  it('adds Authorization: Bearer <token> when a token is present', async () => {
    setSession('t-current', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    const response = await apiFetch({ path: '/api/v1/things' })

    expect(response.status).toBe(200)
    const call = harness.calls[0]!
    expect((call.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-current')
  })

  it('omits Authorization when no token is present', async () => {
    const harness = stubFetch()
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    await apiFetch({ path: '/api/v1/things' })

    const call = harness.calls[0]!
    expect((call.init!.headers as Record<string, string>).Authorization).toBeUndefined()
  })

  it('OVERWRITES a caller-supplied Authorization header with the current session token', async () => {
    setSession('t-store', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    await apiFetch({ path: '/api/v1/things', headers: { Authorization: 'Bearer caller-override' } })

    const call = harness.calls[0]!
    expect((call.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-store')
  })

  it('REMOVES a caller-supplied Authorization header when no session token exists', async () => {
    const harness = stubFetch()
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    await apiFetch({ path: '/api/v1/things', headers: { Authorization: 'Bearer caller-override' } })

    const call = harness.calls[0]!
    expect((call.init!.headers as Record<string, string>).Authorization).toBeUndefined()
  })
})

describe('apiClient 200 path', () => {
  it('passes 200 responses through unchanged', async () => {
    const harness = stubFetch()
    harness.queue.push(async () => jsonBody({ id: 42 }, 200))

    const response = await apiFetch({ path: '/api/v1/things/42' })

    expect(response.status).toBe(200)
    expect(harness.calls).toHaveLength(1)
  })
})

// ────────────────────────────────────────────────────────────────────────
// Origin snapshot ordering (Fix 1): epoch read BEFORE fetch dispatch
// ────────────────────────────────────────────────────────────────────────

describe('apiClient origin-snapshot ordering', () => {
  it('reads the shared epoch BEFORE dispatching the first fetch', async () => {
    setSession('t', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const order: string[] = []

    // Record the epoch read (getItem on the epoch key) and the fetch dispatch
    // in a single ordered log. This test FAILS if fetch is dispatched before
    // the origin epoch snapshot is captured.
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation((key: string) => {
      if (key === SESSION_EPOCH_STORAGE_KEY) {
        order.push('epoch-read')
        return EPOCH_A
      }
      return null
    })
    const harness = stubFetch()
    harness.queue.push(async () => {
      order.push('fetch')
      return jsonBody({ ok: true }, 200)
    })

    await apiFetch({ path: '/api/v1/things' })

    expect(order).toContain('epoch-read')
    expect(order).toContain('fetch')
    expect(order.indexOf('epoch-read')).toBeLessThan(order.indexOf('fetch'))
    // And the very first recorded event is the epoch read.
    expect(order[0]).toBe('epoch-read')
  })
})

// ────────────────────────────────────────────────────────────────────────
// Normal same-session refresh
// ────────────────────────────────────────────────────────────────────────

describe('apiClient 401 → refresh → retry (same session)', () => {
  it('retries exactly once with the refreshed token and returns the retry result', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    const response = await apiFetch({ path: '/api/v1/things' })

    expect(response.status).toBe(200)
    expect(harness.calls).toHaveLength(3)
    expect(harness.calls[0]!.input).toBe('/api/v1/things')
    expect((harness.calls[0]!.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-old')
    expect(harness.calls[1]!.input).toBe(AUTH_REFRESH_PATH)
    expect(harness.calls[2]!.input).toBe('/api/v1/things')
    expect((harness.calls[2]!.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-new')
    expect(getAccessToken()).toBe('t-new')
    expect(getPrincipal()).toEqual(PRINCIPAL_A)
  })

  it('propagates the retry 401 without a second refresh and invalidates the session', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => new Response(null, { status: 401 }))

    const response = await apiFetch({ path: '/api/v1/things' })

    expect(response.status).toBe(401)
    expect(harness.calls).toHaveLength(3)
    expect(harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)).toHaveLength(1)
    expect(getAccessToken()).toBeNull()
    expect(currentGeneration()).toBe(initialGeneration + 1)
  })

  it('propagates the original 401 without retry when refresh itself fails and invalidates the session', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => new Response(null, { status: 401 }))

    const response = await apiFetch({ path: '/api/v1/things' })

    expect(response.status).toBe(401)
    expect(harness.calls).toHaveLength(2)
    expect(harness.calls[0]!.input).toBe('/api/v1/things')
    expect(harness.calls[1]!.input).toBe(AUTH_REFRESH_PATH)
    expect(getAccessToken()).toBeNull()
    expect(currentGeneration()).toBe(initialGeneration + 1)
  })
})

describe('apiClient auth-endpoint exclusion (exact match)', () => {
  it.each([
    ['login', AUTH_LOGIN_PATH],
    ['refresh', AUTH_REFRESH_PATH],
    ['logout', AUTH_LOGOUT_PATH],
  ] as const)('does NOT trigger refresh when %s returns 401', async (_label, path) => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))

    const response = await apiFetch({ path })

    expect(response.status).toBe(401)
    expect(harness.calls).toHaveLength(1)
    expect(harness.calls[0]!.input).toBe(path)
    expect(harness.calls.some((c) => c.input === AUTH_REFRESH_PATH && c !== harness.calls[0])).toBe(false)
  })
})

describe('apiClient handles /api/v1/auth/me like a normal request', () => {
  it('refreshes once and retries /me exactly once when the first /me returns 401', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => jsonBody(sampleLogin('t-new').user, 200))

    const response = await apiFetch({ path: AUTH_ME_PATH })

    expect(response.status).toBe(200)
    expect(harness.calls).toHaveLength(3)
    expect(harness.calls[0]!.input).toBe(AUTH_ME_PATH)
    expect((harness.calls[0]!.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-old')
    expect(harness.calls[1]!.input).toBe(AUTH_REFRESH_PATH)
    expect(harness.calls[2]!.input).toBe(AUTH_ME_PATH)
    expect((harness.calls[2]!.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-new')
    expect(getAccessToken()).toBe('t-new')
  })

  it('propagates the retry 401 on /me without a second refresh and invalidates the session', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => new Response(null, { status: 401 }))

    const response = await apiFetch({ path: AUTH_ME_PATH })

    expect(response.status).toBe(401)
    expect(harness.calls).toHaveLength(3)
    expect(harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)).toHaveLength(1)
    expect(getAccessToken()).toBeNull()
    expect(currentGeneration()).toBe(initialGeneration + 1)
  })
})

// ────────────────────────────────────────────────────────────────────────
// Late-401 fast path (same session)
// ────────────────────────────────────────────────────────────────────────

describe('apiClient late-401 fast path (same session)', () => {
  it('retries once with the already-refreshed token WITHOUT a second refresh when epoch + principal are unchanged', async () => {
    setSession('token-A', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()
    const firstGate = defer<Response>()
    harness.queue.push(async () => firstGate.promise) // primary, gated
    harness.queue.push(async () => jsonBody({ ok: true }, 200)) // retry

    const promise = apiFetch({ path: '/api/v1/mutate', method: 'POST', body: 'x' })

    // Same session refreshed the token in another flow (epoch + principal stay).
    setSession('token-A2', PRINCIPAL_A, EPOCH_A, currentGeneration())

    firstGate.resolve(new Response(null, { status: 401 }))
    const response = await promise

    expect(response.status).toBe(200)
    expect(harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)).toHaveLength(0)
    expect(harness.calls).toHaveLength(2)
    expect((harness.calls[1]!.init!.headers as Record<string, string>).Authorization).toBe('Bearer token-A2')
  })

  it('reuses the already-refreshed token for a 401 that arrives AFTER refresh completed — no second refresh', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()
    const bFirstGate = defer<Response>()

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => bFirstGate.promise)
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => jsonBody({ id: 'a' }, 200))
    harness.queue.push(async () => jsonBody({ id: 'b' }, 200))

    const pA = apiFetch({ path: '/api/v1/a' })
    const pB = apiFetch({ path: '/api/v1/b' })

    const respA = await pA
    expect(respA.status).toBe(200)
    expect(getAccessToken()).toBe('t-new')

    bFirstGate.resolve(new Response(null, { status: 401 }))
    const respB = await pB
    expect(respB.status).toBe(200)

    expect(harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)).toHaveLength(1)
    expect((harness.calls[3]!.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-new')
    expect((harness.calls[4]!.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-new')
    expect(harness.calls).toHaveLength(5)
  })

  it('invalidates the session if the post-race retry (already-refreshed token) also returns 401', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()

    const bFirstGate = defer<Response>()
    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => bFirstGate.promise)
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => jsonBody({ id: 'a' }, 200))
    harness.queue.push(async () => new Response(null, { status: 401 }))

    const pA = apiFetch({ path: '/api/v1/a' })
    const pB = apiFetch({ path: '/api/v1/b' })

    await pA
    bFirstGate.resolve(new Response(null, { status: 401 }))
    const respB = await pB

    expect(respB.status).toBe(401)
    expect(harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)).toHaveLength(1)
    expect(getAccessToken()).toBeNull()
    expect(currentGeneration()).toBe(initialGeneration + 1)
  })
})

// ────────────────────────────────────────────────────────────────────────
// CROSS-TAB session replacement (the P1)
// ────────────────────────────────────────────────────────────────────────

describe('apiClient cross-tab session replacement', () => {
  // Test B: a request that STARTS after a sibling already replaced the session
  // is rejected pre-dispatch with a TYPED error (never a fabricated 401) and
  // ZERO fetch. The distinction from unreadable-epoch (ApiUnavailableError) is
  // covered by the "cannot be READ" test above.
  it('throws SessionReplacedError with ZERO fetch when the local bound epoch != a READABLE shared epoch', async () => {
    setSession('token-A', PRINCIPAL_A, EPOCH_A, currentGeneration())
    // Sibling ALREADY replaced the session before this request begins.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)
    const harness = stubFetch()

    await expect(apiFetch({ path: '/api/v1/mutate', method: 'POST', body: 'x' }))
      .rejects.toBeInstanceOf(SessionReplacedError)
    // NOTHING was dispatched — not even the primary request.
    expect(harness.calls).toHaveLength(0)
    // The stale local session is left untouched (not silently mutated).
    expect(getAccessToken()).toBe('token-A')
    expect(getBoundEpoch()).toBe(EPOCH_A)
  })

  // Test C: same-account replacement (identical principal) is still blocked,
  // because the epoch — not identity — is the session boundary.
  it('throws SessionReplacedError pre-dispatch even when the replacing session is the SAME account (epoch != identity)', async () => {
    setSession('token-A', PRINCIPAL_A, EPOCH_A, currentGeneration())
    // Sibling logged into the SAME account; principal is unchanged but a new
    // login is a new session boundary, so the epoch rotated.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)
    const harness = stubFetch()

    await expect(apiFetch({ path: '/api/v1/mutate', method: 'POST', body: 'x' }))
      .rejects.toBeInstanceOf(SessionReplacedError)
    expect(harness.calls).toHaveLength(0)
  })

  it('does NOT POST /refresh or retry when a sibling rotated the epoch while the request was in flight', async () => {
    setSession('token-A', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()
    const firstGate = defer<Response>()
    harness.queue.push(async () => firstGate.promise) // primary, gated

    const promise = apiFetch({ path: '/api/v1/mutate', method: 'POST', body: 'x' })

    // Sibling tab logs in as B — rotate the shared epoch while A is in flight.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)

    firstGate.resolve(new Response(null, { status: 401 }))
    const response = await promise

    expect(response.status).toBe(401)
    // Only the original request happened — no /refresh, no retry.
    expect(harness.calls).toHaveLength(1)
    expect(harness.calls[0]!.input).toBe('/api/v1/mutate')
    expect(harness.calls.some((c) => c.input === AUTH_REFRESH_PATH)).toBe(false)
    // No sibling (B) token installed into the old A flow.
    expect(getAccessToken()).toBe('token-A')
    expect(getPrincipal()).toEqual(PRINCIPAL_A)
  })

  it('sees the epoch change WHILE WAITING for the refresh lock and neither POSTs /refresh nor retries', async () => {
    setSession('token-A', PRINCIPAL_A, EPOCH_A, currentGeneration())
    uninstallLocks()
    const gated = installGatedLocks()
    const harness = stubFetch()
    harness.queue.push(async () => new Response(null, { status: 401 })) // primary 401 (immediate)

    const promise = apiFetch({ path: '/api/v1/mutate', method: 'POST', body: 'x' })

    // Deterministically wait until refreshOnce has requested the lock.
    await gated.requested

    // Sibling login rotates the epoch while we are queued behind the lock.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)

    gated.release()
    const response = await promise

    expect(response.status).toBe(401)
    // Zero POST /refresh — the post-lock epoch check aborted before the POST.
    expect(harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)).toHaveLength(0)
    expect(harness.calls).toHaveLength(1) // only the primary request
    expect(getAccessToken()).toBe('token-A')
  })

  it('same-account login (identical principal) STILL blocks the old request because the epoch changed (epoch != identity)', async () => {
    setSession('token-A', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()
    const firstGate = defer<Response>()
    harness.queue.push(async () => firstGate.promise)

    const promise = apiFetch({ path: '/api/v1/mutate', method: 'POST', body: 'x' })

    // Sibling logs into the SAME account — principal is unchanged, but a new
    // login is a new session boundary, so the epoch rotates.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)

    firstGate.resolve(new Response(null, { status: 401 }))
    const response = await promise

    expect(response.status).toBe(401)
    expect(harness.calls).toHaveLength(1)
    expect(harness.calls.some((c) => c.input === AUTH_REFRESH_PATH)).toBe(false)
  })

  it('does NOT replay AND does NOT install a foreign token when the refreshed PRINCIPAL differs (epoch unchanged)', async () => {
    setSession('token-A', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()
    harness.queue.push(async () => new Response(null, { status: 401 })) // primary
    harness.queue.push(async () => jsonBody(sampleLoginAs('token-B', PRINCIPAL_B), 200)) // refresh → B identity

    const response = await apiFetch({ path: '/api/v1/mutate', method: 'POST', body: 'x' })

    expect(response.status).toBe(401)
    // Refresh happened (epoch matched), but the token was rejected BEFORE
    // install by refreshOnce, and the original request was NOT replayed.
    expect(harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)).toHaveLength(1)
    expect(harness.calls).toHaveLength(2) // primary + refresh only, no retry
    // Token B / principal B are NOT in the store — A survives untouched.
    expect(getAccessToken()).toBe('token-A')
    expect(getPrincipal()).toEqual(PRINCIPAL_A)
  })

  it('late-401: does NOT retry when BOTH the token and the epoch changed (session replaced)', async () => {
    setSession('token-A', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()
    const firstGate = defer<Response>()
    harness.queue.push(async () => firstGate.promise)

    const promise = apiFetch({ path: '/api/v1/mutate', method: 'POST', body: 'x' })

    // Sibling replaced the session entirely: new token + principal + epoch.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)
    setSession('token-B', PRINCIPAL_B, EPOCH_B, currentGeneration())

    firstGate.resolve(new Response(null, { status: 401 }))
    const response = await promise

    expect(response.status).toBe(401)
    expect(harness.calls).toHaveLength(1) // no retry, no refresh
    expect(harness.calls.some((c) => c.input === AUTH_REFRESH_PATH)).toBe(false)
    // The new session's token is preserved (an old request must not clear it).
    expect(getAccessToken()).toBe('token-B')
  })
})

// ────────────────────────────────────────────────────────────────────────
// Temporary refresh failure
// ────────────────────────────────────────────────────────────────────────

describe('apiClient refresh unavailability (temporary failure)', () => {
  it('REJECTS with ApiUnavailableError when refresh throws a network error; session state preserved', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => {
      throw new TypeError('network down')
    })

    await expect(apiFetch({ path: '/api/v1/things' })).rejects.toBeInstanceOf(ApiUnavailableError)
    expect(getAccessToken()).toBe('t-old')
    expect(currentGeneration()).toBe(initialGeneration)
  })

  it('carries the underlying cause on the ApiUnavailableError', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()

    const underlyingError = new TypeError('network down')
    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => {
      throw underlyingError
    })

    let caught: unknown
    try {
      await apiFetch({ path: '/api/v1/things' })
    } catch (error) {
      caught = error
    }
    expect(caught).toBeInstanceOf(ApiUnavailableError)
    expect((caught as ApiUnavailableError).cause).toBe(underlyingError)
  })

  it('REJECTS with ApiUnavailableError when refresh receives an unexpected server status (e.g. 500)', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => new Response(null, { status: 500 }))

    await expect(apiFetch({ path: '/api/v1/things' })).rejects.toBeInstanceOf(ApiUnavailableError)
    expect(getAccessToken()).toBe('t-old')
    expect(currentGeneration()).toBe(initialGeneration)
  })

  it('fails closed (ApiUnavailableError) and dispatches NOTHING when the shared epoch cannot be READ at capture', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()

    // Break epoch READ. apiFetch must reject at capture — BEFORE any fetch —
    // so an unreadable epoch can never be treated as a comparable value.
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('denied')
    })

    await expect(apiFetch({ path: '/api/v1/things' })).rejects.toBeInstanceOf(ApiUnavailableError)
    // ZERO fetches — not even the primary request was dispatched.
    expect(harness.calls).toHaveLength(0)
    expect(getAccessToken()).toBe('t-old')
    expect(currentGeneration()).toBe(initialGeneration)
  })

  it('lets a later apiFetch attempt a fresh refresh cycle after connectivity recovers', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => {
      throw new TypeError('network down')
    })

    await expect(apiFetch({ path: '/api/v1/first' })).rejects.toBeInstanceOf(ApiUnavailableError)
    expect(getAccessToken()).toBe('t-old')

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    const secondResponse = await apiFetch({ path: '/api/v1/second' })

    expect(secondResponse.status).toBe(200)
    expect(getAccessToken()).toBe('t-new')
    expect(harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)).toHaveLength(2)
  })
})

// ────────────────────────────────────────────────────────────────────────
// Case-insensitive Authorization stripping
// ────────────────────────────────────────────────────────────────────────

describe('apiClient case-insensitive Authorization stripping', () => {
  it('strips a caller-supplied lowercase `authorization` and applies the session bearer', async () => {
    setSession('t-store', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    await apiFetch({ path: '/api/v1/things', headers: { authorization: 'Bearer caller-lc' } })

    const outgoing = harness.calls[0]!.init!.headers as Record<string, string>
    expect(outgoing.authorization).toBeUndefined()
    expect(outgoing.Authorization).toBe('Bearer t-store')
  })

  it('strips a caller-supplied mixed-case `AuThOrIzAtIoN` just as reliably', async () => {
    setSession('t-store', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    await apiFetch({ path: '/api/v1/things', headers: { AuThOrIzAtIoN: 'Bearer caller-mc' } })

    const outgoing = harness.calls[0]!.init!.headers as Record<string, string>
    expect(outgoing.AuThOrIzAtIoN).toBeUndefined()
    expect(outgoing.Authorization).toBe('Bearer t-store')
  })
})

// ────────────────────────────────────────────────────────────────────────
// Terminal-401 token-identity guard (deterministic, no timers)
// ────────────────────────────────────────────────────────────────────────

describe('apiClient terminal-401 token-identity guard', () => {
  it('does NOT invalidate the session when a newer token replaced the one used by the failing retry', async () => {
    setSession('A', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()
    const retryStarted = defer<void>()
    const retryGate = defer<Response>()

    harness.queue.push(async () => new Response(null, { status: 401 })) // primary
    harness.queue.push(async () => jsonBody(sampleLogin('B'), 200)) // refresh → B
    harness.queue.push(async () => {
      // Signal that the retry fetch has started, then wait on the gate.
      retryStarted.resolve()
      return retryGate.promise
    })

    const promise = apiFetch({ path: '/api/v1/things' })

    // Deterministic: the retry fetch has been dispatched, so refresh installed B.
    await retryStarted.promise
    expect(getAccessToken()).toBe('B')

    // A newer same-session refresh installs C while the retry (carrying B) is
    // still gated. Same generation — token identity is the only reliable guard.
    setSession('C', PRINCIPAL_A, EPOCH_A, currentGeneration())
    expect(getAccessToken()).toBe('C')

    retryGate.resolve(new Response(null, { status: 401 }))
    const response = await promise

    expect(response.status).toBe(401)
    // C must survive — the terminal path used B's identity, not C's.
    expect(getAccessToken()).toBe('C')
    expect(currentGeneration()).toBe(initialGeneration)
  })
})

// ────────────────────────────────────────────────────────────────────────
// Body type contract (replayable only)
// ────────────────────────────────────────────────────────────────────────

describe('apiClient body type contract (replayable only)', () => {
  // Compile-time regression guard: if a future edit ever widens
  // ApiRequest['body'] back to include ReadableStream, the directive on the
  // assignment below will stop matching a real type error and tsc will flag
  // it as unused, failing the build.
  it('type-check: ReadableStream is NOT assignable to ApiRequest.body', () => {
    // @ts-expect-error - ReadableStream must be excluded from ApiRequest bodies.
    const _invalid: ApiRequest = { path: '/x', body: new ReadableStream() }
    void _invalid
  })

  it('rejects apiFetch at runtime when a ReadableStream body slips through the type contract', async () => {
    const stream = new ReadableStream()
    await expect(
      apiFetch({
        path: '/api/v1/upload',
        method: 'POST',
        body: stream as unknown as XMLHttpRequestBodyInit,
      }),
    ).rejects.toThrow(/replayable|ReadableStream/i)
  })

  it('accepts a FormData body and replays it verbatim across a refresh retry', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()
    const form = new FormData()
    form.append('field', 'value')
    form.append('file', new Blob(['abc']), 'a.txt')

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    const response = await apiFetch({ path: '/api/v1/upload', method: 'POST', body: form })

    expect(response.status).toBe(200)
    expect(harness.calls).toHaveLength(3)
    expect(harness.calls[0]!.init!.body).toBe(form)
    expect(harness.calls[2]!.init!.body).toBe(form)
    expect(Array.from(form.entries())).toHaveLength(2)
    expect((harness.calls[2]!.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-new')
  })

  it('accepts a URLSearchParams body and replays it across a refresh retry', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()
    const params = new URLSearchParams([['a', '1'], ['b', '2']])

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    const response = await apiFetch({ path: '/api/v1/form', method: 'POST', body: params })

    expect(response.status).toBe(200)
    expect(harness.calls[0]!.init!.body).toBe(params)
    expect(harness.calls[2]!.init!.body).toBe(params)
    expect(params.toString()).toBe('a=1&b=2')
  })
})

// ────────────────────────────────────────────────────────────────────────
// Post-refresh race: sibling rotates epoch AFTER refreshOnce returns but
// BEFORE apiClient replays the original request. The epoch under which
// the refresh succeeded must still be current at retry time.
// ────────────────────────────────────────────────────────────────────────

// Installs a fake lock manager that runs the callback, then — AFTER the
// callback resolves but BEFORE the result propagates to the caller —
// rotates the shared epoch. This deterministically places the sibling
// login in the exact window between the lock release and apiClient's
// post-refresh check, with no timers.
function installPostLockEpochRotation(newEpoch: string): void {
  Object.defineProperty(navigator, 'locks', {
    configurable: true,
    value: {
      request: async (_name: string, ...args: unknown[]) => {
        const callback = (typeof args[0] === 'function' ? args[0] : args[1]) as (
          lock: unknown,
        ) => Promise<unknown>
        const result = await callback({})
        localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, newEpoch)
        return result
      },
    },
  })
}

describe('apiClient post-refresh epoch race', () => {
  it('MISSING bootstrap: throws SessionReplacedError when a sibling rotates the epoch after refresh establishes E1', async () => {
    localStorage.clear() // no epoch — MISSING
    // No local session — cold bootstrap
    installPostLockEpochRotation(EPOCH_B)
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 })) // primary
    harness.queue.push(async () => jsonBody(sampleLogin('t-boot'), 200)) // refresh → establishes E1
    // NO retry response queued — proves zero retry dispatch.

    await expect(apiFetch({ path: '/api/v1/cold' }))
      .rejects.toBeInstanceOf(SessionReplacedError)
    // Primary + refresh only — ZERO application retry.
    expect(harness.calls).toHaveLength(2)
    expect(harness.calls[0]!.input).toBe('/api/v1/cold')
    expect(harness.calls[1]!.input).toBe(AUTH_REFRESH_PATH)
    // The sibling's E2 epoch is left untouched.
    expect(localStorage.getItem(SESSION_EPOCH_STORAGE_KEY)).toBe(EPOCH_B)
  })

  it('PRESENT-E: throws SessionReplacedError when a sibling rotates the epoch after refresh succeeds under E1', async () => {
    setSession('token-A', PRINCIPAL_A, EPOCH_A, currentGeneration())
    installPostLockEpochRotation(EPOCH_B)
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 })) // primary
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200)) // refresh under EPOCH_A
    // NO retry response queued.

    await expect(apiFetch({ path: '/api/v1/mutate', method: 'POST', body: 'x' }))
      .rejects.toBeInstanceOf(SessionReplacedError)
    expect(harness.calls).toHaveLength(2)
    expect(harness.calls[0]!.input).toBe('/api/v1/mutate')
    expect(harness.calls[1]!.input).toBe(AUTH_REFRESH_PATH)
    // Sibling's E2 epoch untouched.
    expect(localStorage.getItem(SESSION_EPOCH_STORAGE_KEY)).toBe(EPOCH_B)
  })

  it('MISSING bootstrap: retries normally when the established epoch remains current', async () => {
    localStorage.clear() // no epoch — MISSING
    // Passthrough locks (default beforeEach), no post-lock rotation.
    installPassthroughLocks()
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 })) // primary
    harness.queue.push(async () => jsonBody(sampleLogin('t-boot'), 200)) // refresh → establishes epoch
    harness.queue.push(async () => jsonBody({ ok: true }, 200)) // retry

    const response = await apiFetch({ path: '/api/v1/cold' })

    expect(response.status).toBe(200)
    expect(harness.calls).toHaveLength(3)
    expect(harness.calls[0]!.input).toBe('/api/v1/cold')
    expect(harness.calls[1]!.input).toBe(AUTH_REFRESH_PATH)
    expect(harness.calls[2]!.input).toBe('/api/v1/cold')
    // Retry used the refreshed token.
    expect((harness.calls[2]!.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-boot')
    // Session is bound to the established epoch.
    const established = localStorage.getItem(SESSION_EPOCH_STORAGE_KEY)
    expect(established).not.toBeNull()
    expect(getAccessToken()).toBe('t-boot')
    expect(getPrincipal()).toEqual(PRINCIPAL_A)
    expect(getBoundEpoch()).toBe(established)
  })
})

// ────────────────────────────────────────────────────────────────────────
// Concurrent 401s (deterministic — single-flight keeps pending alive)
// ────────────────────────────────────────────────────────────────────────

describe('apiClient concurrent 401s', () => {
  it('issues exactly one POST /refresh for many concurrent 401 responses', async () => {
    setSession('t-old', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const harness = stubFetch()

    // Three primaries (called synchronously → dequeued first), then one
    // refresh, then three retries. The single-flight pending stays alive
    // until the refresh resolves, which is deterministically after all three
    // callers have shared it — no timer needed.
    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => jsonBody({ id: 'a' }, 200))
    harness.queue.push(async () => jsonBody({ id: 'b' }, 200))
    harness.queue.push(async () => jsonBody({ id: 'c' }, 200))

    const [r1, r2, r3] = await Promise.all([
      apiFetch({ path: '/api/v1/things/a' }),
      apiFetch({ path: '/api/v1/things/b' }),
      apiFetch({ path: '/api/v1/things/c' }),
    ])

    expect(r1.status).toBe(200)
    expect(r2.status).toBe(200)
    expect(r3.status).toBe(200)
    expect(harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)).toHaveLength(1)
    expect(getAccessToken()).toBe('t-new')
  })
})
