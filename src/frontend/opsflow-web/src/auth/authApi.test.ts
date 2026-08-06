import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  AUTH_LOGIN_PATH,
  AUTH_LOGOUT_PATH,
  AUTH_ME_PATH,
  AUTH_REFRESH_PATH,
  login,
  logout,
  me,
  refresh,
} from './authApi'
import { AUTH_COOKIE_LOCK_NAME } from './authCookieLock'
import type { LoginResponse, LoginUserResponse } from './contracts'

interface MockFetchCall {
  input: RequestInfo | URL
  init: RequestInit | undefined
}

function stubFetch(): { calls: MockFetchCall[]; setNext: (response: Response) => void } {
  const calls: MockFetchCall[] = []
  const queue: Response[] = []

  const mock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ input, init })
    const next = queue.shift()
    if (next === undefined) {
      throw new Error('fetch called with no queued response')
    }
    return next
  })

  vi.stubGlobal('fetch', mock)

  return {
    calls,
    setNext: (response) => {
      queue.push(response)
    },
  }
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

const sampleUser: LoginUserResponse = {
  userId: '11111111-1111-1111-1111-111111111111',
  email: 'user@test.local',
  displayName: 'Test User',
  organizationId: '22222222-2222-2222-2222-222222222222',
  organizationName: 'Test Org',
  roles: ['Coordinator'],
}

const sampleLogin: LoginResponse = {
  accessToken: 'access-token-123',
  accessTokenExpiresAt: '2030-01-01T00:00:00.000+00:00',
  user: sampleUser,
}

// ────────────────────────────────────────────────────────────────────────
// Fake Web Locks — login and logout now serialise through
// `withAuthCookieLock`, so tests must install a compatible `navigator.locks`.
// ────────────────────────────────────────────────────────────────────────

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

interface FakeLockHarness {
  manager: unknown
  requestCalls: Array<{ name: string }>
  acquireGate?: Deferred<void>
  throwOnAcquire?: unknown
}

function makeFakeLockManager(): FakeLockHarness {
  const harness: FakeLockHarness = { manager: undefined, requestCalls: [] }
  harness.manager = {
    request: async (name: string, ...args: unknown[]) => {
      const callback = (typeof args[0] === 'function' ? args[0] : args[1]) as (
        lock: unknown,
      ) => Promise<unknown>
      harness.requestCalls.push({ name })
      if (harness.throwOnAcquire !== undefined) {
        throw harness.throwOnAcquire
      }
      if (harness.acquireGate !== undefined) {
        await harness.acquireGate.promise
      }
      return await callback({ name })
    },
  }
  return harness
}

function installFakeLocks(manager: unknown): void {
  Object.defineProperty(navigator, 'locks', { configurable: true, value: manager })
}

function uninstallLocks(): void {
  Object.defineProperty(navigator, 'locks', { configurable: true, value: undefined })
}

let defaultLockHarness: FakeLockHarness | null = null

beforeEach(() => {
  // Default: passthrough fake so pre-existing happy-path tests continue to
  // run under a Web-Locks-available environment. Coordination-specific
  // tests below replace it with a gated / throwing / missing stub.
  defaultLockHarness = makeFakeLockManager()
  installFakeLocks(defaultLockHarness.manager)
})

afterEach(() => {
  vi.unstubAllGlobals()
  uninstallLocks()
  defaultLockHarness = null
})

describe('authApi.login', () => {
  let harness: ReturnType<typeof stubFetch>
  beforeEach(() => {
    harness = stubFetch()
  })

  it('POSTs email/password to the login path with credentials: include and JSON body', async () => {
    harness.setNext(jsonResponse(sampleLogin, 200))

    const result = await login({ email: 'user@test.local', password: 'pw' })

    expect(result).toEqual({ kind: 'success', data: sampleLogin })
    expect(harness.calls).toHaveLength(1)
    const call = harness.calls[0]!
    expect(call.input).toBe(AUTH_LOGIN_PATH)
    expect(call.init?.method).toBe('POST')
    expect(call.init?.credentials).toBe('include')
    expect((call.init!.headers as Record<string, string>)['Content-Type']).toBe('application/json')
    expect(call.init?.body).toBe(JSON.stringify({ email: 'user@test.local', password: 'pw' }))
  })

  it('maps 401 to invalid-credentials', async () => {
    harness.setNext(new Response(null, { status: 401 }))
    await expect(login({ email: 'x', password: 'y' })).resolves.toEqual({ kind: 'invalid-credentials' })
  })

  it('maps 400 to validation-failed', async () => {
    harness.setNext(new Response(null, { status: 400 }))
    await expect(login({ email: 'x', password: 'y' })).resolves.toEqual({ kind: 'validation-failed' })
  })

  it('maps thrown fetch errors to unavailable', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => {
      throw new TypeError('network down')
    }))
    const result = await login({ email: 'x', password: 'y' })
    expect(result.kind).toBe('unavailable')
  })
})

describe('authApi.refresh', () => {
  let harness: ReturnType<typeof stubFetch>
  beforeEach(() => {
    harness = stubFetch()
  })

  it('POSTs the refresh path with credentials: include and no body', async () => {
    harness.setNext(jsonResponse(sampleLogin, 200))

    const result = await refresh()

    expect(result).toEqual({ kind: 'success', data: sampleLogin })
    const call = harness.calls[0]!
    expect(call.input).toBe(AUTH_REFRESH_PATH)
    expect(call.init?.method).toBe('POST')
    expect(call.init?.credentials).toBe('include')
    expect(call.init?.body).toBeUndefined()
  })

  it('maps 401 to unauthenticated', async () => {
    harness.setNext(new Response(null, { status: 401 }))
    await expect(refresh()).resolves.toEqual({ kind: 'unauthenticated' })
  })
})

describe('authApi.logout', () => {
  let harness: ReturnType<typeof stubFetch>
  beforeEach(() => {
    harness = stubFetch()
  })

  it('POSTs the logout path with credentials: include and treats 204 as success', async () => {
    harness.setNext(new Response(null, { status: 204 }))

    const result = await logout()

    expect(result).toEqual({ kind: 'success' })
    const call = harness.calls[0]!
    expect(call.input).toBe(AUTH_LOGOUT_PATH)
    expect(call.init?.method).toBe('POST')
    expect(call.init?.credentials).toBe('include')
  })
})

describe('authApi.me', () => {
  let harness: ReturnType<typeof stubFetch>
  beforeEach(() => {
    harness = stubFetch()
  })

  it('GETs the me path with a Bearer token supplied by the caller and no cookie credential', async () => {
    harness.setNext(jsonResponse(sampleUser, 200))

    const result = await me('access-token-abc')

    expect(result).toEqual({ kind: 'success', data: sampleUser })
    const call = harness.calls[0]!
    expect(call.input).toBe(AUTH_ME_PATH)
    expect(call.init?.method).toBe('GET')
    expect((call.init!.headers as Record<string, string>).Authorization).toBe('Bearer access-token-abc')
    expect(call.init?.credentials).toBeUndefined()
  })

  it('maps 401 to unauthenticated', async () => {
    harness.setNext(new Response(null, { status: 401 }))
    await expect(me('t')).resolves.toEqual({ kind: 'unauthenticated' })
  })
})

// ────────────────────────────────────────────────────────────────────────
// authApi.login — auth cookie lock coordination
// ────────────────────────────────────────────────────────────────────────

describe('authApi.login — auth cookie lock coordination', () => {
  it('acquires the SHARED auth-cookie lock (same constant as refresh/logout) before POST /login', async () => {
    const fetchHarness = stubFetch()
    fetchHarness.setNext(jsonResponse(sampleLogin, 200))

    await login({ email: 'u', password: 'p' })

    expect(defaultLockHarness!.requestCalls).toEqual([{ name: AUTH_COOKIE_LOCK_NAME }])
  })

  it('POST /login is issued INSIDE the lock callback (proof by fetch-count-before/after gate release)', async () => {
    // Replace default passthrough with a gated lock so we can observe the
    // strict ordering: no fetch before the lock's callback runs.
    uninstallLocks()
    const gated = makeFakeLockManager()
    gated.acquireGate = defer<void>()
    installFakeLocks(gated.manager)

    const fetchHarness = stubFetch()
    fetchHarness.setNext(jsonResponse(sampleLogin, 200))

    const promise = login({ email: 'u', password: 'p' })

    // Lock is requested but callback is suspended on the gate → no fetch yet.
    expect(gated.requestCalls).toEqual([{ name: AUTH_COOKIE_LOCK_NAME }])
    expect(fetchHarness.calls).toHaveLength(0)

    // Release the lock and await the whole cycle — deterministic sync point.
    gated.acquireGate.resolve()
    const result = await promise

    expect(result.kind).toBe('success')
    expect(fetchHarness.calls).toHaveLength(1)
    expect(fetchHarness.calls[0]!.input).toBe(AUTH_LOGIN_PATH)
  })

  it('returns unavailable and issues ZERO POST /login when navigator.locks is missing', async () => {
    uninstallLocks()
    const fetchHarness = stubFetch()

    const result = await login({ email: 'u', password: 'p' })

    expect(result.kind).toBe('unavailable')
    expect(fetchHarness.calls).toHaveLength(0)
  })

  it('returns unavailable and issues ZERO POST /login when lock acquisition throws', async () => {
    uninstallLocks()
    const throwing = makeFakeLockManager()
    throwing.throwOnAcquire = new Error('lock rejected')
    installFakeLocks(throwing.manager)

    const fetchHarness = stubFetch()

    const result = await login({ email: 'u', password: 'p' })

    expect(result.kind).toBe('unavailable')
    expect(fetchHarness.calls).toHaveLength(0)
  })
})

// ────────────────────────────────────────────────────────────────────────
// authApi.logout — auth cookie lock coordination
// ────────────────────────────────────────────────────────────────────────

describe('authApi.logout — auth cookie lock coordination', () => {
  it('acquires the SHARED auth-cookie lock (same constant as refresh/login) before POST /logout', async () => {
    const fetchHarness = stubFetch()
    fetchHarness.setNext(new Response(null, { status: 204 }))

    await logout()

    expect(defaultLockHarness!.requestCalls).toEqual([{ name: AUTH_COOKIE_LOCK_NAME }])
  })

  it('POST /logout is issued INSIDE the lock callback (proof by fetch-count-before/after gate release)', async () => {
    uninstallLocks()
    const gated = makeFakeLockManager()
    gated.acquireGate = defer<void>()
    installFakeLocks(gated.manager)

    const fetchHarness = stubFetch()
    fetchHarness.setNext(new Response(null, { status: 204 }))

    const promise = logout()

    expect(gated.requestCalls).toEqual([{ name: AUTH_COOKIE_LOCK_NAME }])
    expect(fetchHarness.calls).toHaveLength(0)

    gated.acquireGate.resolve()
    const result = await promise

    expect(result).toEqual({ kind: 'success' })
    expect(fetchHarness.calls).toHaveLength(1)
    expect(fetchHarness.calls[0]!.input).toBe(AUTH_LOGOUT_PATH)
  })

  it('returns unavailable and issues ZERO POST /logout when navigator.locks is missing', async () => {
    uninstallLocks()
    const fetchHarness = stubFetch()

    const result = await logout()

    expect(result.kind).toBe('unavailable')
    expect(fetchHarness.calls).toHaveLength(0)
  })

  it('returns unavailable and issues ZERO POST /logout when lock acquisition throws', async () => {
    uninstallLocks()
    const throwing = makeFakeLockManager()
    throwing.throwOnAcquire = new Error('lock rejected')
    installFakeLocks(throwing.manager)

    const fetchHarness = stubFetch()

    const result = await logout()

    expect(result.kind).toBe('unavailable')
    expect(fetchHarness.calls).toHaveLength(0)
  })
})

// ────────────────────────────────────────────────────────────────────────
// authApi.refresh — low-level primitive, does NOT acquire the lock itself
// (nesting protection: singleFlightRefresh holds the lock and invokes this).
// ────────────────────────────────────────────────────────────────────────

describe('authApi.refresh — low-level primitive (does NOT acquire the cookie lock)', () => {
  it('POSTs /refresh WITHOUT calling navigator.locks (the lock is held by the caller — singleFlightRefresh)', async () => {
    const fetchHarness = stubFetch()
    fetchHarness.setNext(jsonResponse(sampleLogin, 200))

    await refresh()

    // Fetch was made, but the low-level primitive must NOT have acquired
    // the shared lock — that would nest a Web Lock request from inside the
    // caller's already-held lock and deadlock the refresh path.
    expect(fetchHarness.calls).toHaveLength(1)
    expect(fetchHarness.calls[0]!.input).toBe(AUTH_REFRESH_PATH)
    expect(defaultLockHarness!.requestCalls).toHaveLength(0)
  })
})
