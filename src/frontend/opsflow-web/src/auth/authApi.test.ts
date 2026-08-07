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
import { SESSION_EPOCH_STORAGE_KEY, _resetSessionEpochForTests } from './sessionEpoch'

// Raw view of what is actually stored under the epoch key.
function storedEpoch(): string | null {
  return localStorage.getItem(SESSION_EPOCH_STORAGE_KEY)
}

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
  // login/logout now touch the shared epoch (localStorage). Start clean so
  // each test observes only its own rotation.
  _resetSessionEpochForTests()
  localStorage.clear()
})

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  uninstallLocks()
  localStorage.clear()
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

    expect(result.kind).toBe('success')
    if (result.kind === 'success') {
      expect(result.data).toEqual(sampleLogin)
      expect(typeof result.epoch).toBe('string')
    }
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

  it('does NOT rotate the shared session epoch (refresh keeps the SAME logical session)', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-before')
    const fetchHarness = stubFetch()
    fetchHarness.setNext(jsonResponse(sampleLogin, 200))

    await refresh()

    expect(storedEpoch()).toBe('epoch-before')
  })
})

// ────────────────────────────────────────────────────────────────────────
// authApi.login — session epoch rotation
// ────────────────────────────────────────────────────────────────────────

describe('authApi.login — session epoch rotation', () => {
  it('rotates the shared epoch to a NEW value on a successful login', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-before')
    const fetchHarness = stubFetch()
    fetchHarness.setNext(jsonResponse(sampleLogin, 200))

    const result = await login({ email: 'u', password: 'p' })

    expect(result.kind).toBe('success')
    const after = storedEpoch()
    expect(after).not.toBeNull()
    expect(after).not.toBe('epoch-before')
  })

  it('rotates the epoch EVEN when the same account logs in again (new session boundary)', async () => {
    // Establish an epoch as if this account were already logged in.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-existing')
    const fetchHarness = stubFetch()
    fetchHarness.setNext(jsonResponse(sampleLogin, 200)) // same account (sampleUser)

    await login({ email: 'user@test.local', password: 'pw' })

    expect(storedEpoch()).not.toBe('epoch-existing')
  })

  it('does NOT rotate the epoch on 401 invalid-credentials', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-before')
    const fetchHarness = stubFetch()
    fetchHarness.setNext(new Response(null, { status: 401 }))

    const result = await login({ email: 'x', password: 'y' })

    expect(result.kind).toBe('invalid-credentials')
    expect(storedEpoch()).toBe('epoch-before')
  })

  it('does NOT rotate the epoch on 400 validation-failed', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-before')
    const fetchHarness = stubFetch()
    fetchHarness.setNext(new Response(null, { status: 400 }))

    const result = await login({ email: 'x', password: 'y' })

    expect(result.kind).toBe('validation-failed')
    expect(storedEpoch()).toBe('epoch-before')
  })

  it('fails closed with unavailable and ZERO POST /login when epoch storage is unavailable', async () => {
    const fetchHarness = stubFetch()
    fetchHarness.setNext(jsonResponse(sampleLogin, 200))
    // Break epoch storage: the pre-POST precommit (rotateEpoch) throws.
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('QuotaExceededError')
    })

    const result = await login({ email: 'u', password: 'p' })

    expect(result.kind).toBe('unavailable')
    expect(fetchHarness.calls).toHaveLength(0)
  })
})

// ────────────────────────────────────────────────────────────────────────
// authApi — precommit epoch protocol (OLD → NEXT → HTTP → keep/rollback)
// ────────────────────────────────────────────────────────────────────────

describe('authApi.login — precommit protocol', () => {
  // Test F: NEXT is written BEFORE the login POST.
  it('writes NEXT to the shared epoch BEFORE dispatching POST /login (precommit ordering)', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-before')
    const order: string[] = []
    const realSet = Storage.prototype.setItem
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (
      this: Storage,
      key: string,
      value: string,
    ) {
      if (key === SESSION_EPOCH_STORAGE_KEY) order.push('epoch-write')
      return realSet.call(this, key, value)
    })
    const fetchHarness = stubFetch()
    // The fetch mock records the POST /login dispatch.
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      order.push('login-post')
      void input
      return jsonResponse(sampleLogin, 200)
    }))
    void fetchHarness

    const result = await login({ email: 'u', password: 'p' })

    expect(result.kind).toBe('success')
    expect(order[0]).toBe('epoch-write')
    expect(order.indexOf('epoch-write')).toBeLessThan(order.indexOf('login-post'))
  })

  // Test G: success KEEPS NEXT, returns it, and needs NO post-success write.
  it('keeps NEXT and returns the committed epoch; exactly ONE epoch write occurs (no post-success write)', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-before')
    let epochWrites = 0
    const realSet = Storage.prototype.setItem
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (
      this: Storage,
      key: string,
      value: string,
    ) {
      if (key === SESSION_EPOCH_STORAGE_KEY) epochWrites += 1
      return realSet.call(this, key, value)
    })
    const fetchHarness = stubFetch()
    fetchHarness.setNext(jsonResponse(sampleLogin, 200))

    const result = await login({ email: 'u', password: 'p' })

    expect(result.kind).toBe('success')
    if (result.kind === 'success') {
      // The committed epoch is exactly what remains in storage.
      expect(result.epoch).toBe(storedEpoch())
      expect(result.epoch).not.toBe('epoch-before')
    }
    // Precommit is the ONLY epoch write — nothing written after the 200.
    expect(epochWrites).toBe(1)
  })

  // Test H: a 401 rolls the epoch back to OLD.
  it('rolls the epoch back to OLD on a failed (401) login', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-old')
    const fetchHarness = stubFetch()
    fetchHarness.setNext(new Response(null, { status: 401 }))

    const result = await login({ email: 'x', password: 'y' })

    expect(result.kind).toBe('invalid-credentials')
    expect(storedEpoch()).toBe('epoch-old')
  })

  // Test I(1) — AMBIGUOUS network/transport failure after precommit: KEEP NEXT,
  // never roll back (the Set-Cookie may already have been processed).
  it('KEEPS NEXT (no rollback) on a network failure after dispatch', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-old')
    let precommitted: string | null = null
    // Capture the NEXT value the precommit wrote, to prove it is kept.
    const realSet = Storage.prototype.setItem
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (
      this: Storage,
      key: string,
      value: string,
    ) {
      if (key === SESSION_EPOCH_STORAGE_KEY) precommitted = value
      return realSet.call(this, key, value)
    })
    vi.stubGlobal('fetch', vi.fn(async () => {
      throw new TypeError('network down')
    }))

    const result = await login({ email: 'u', password: 'p' })

    expect(result.kind).toBe('unavailable')
    // NEXT kept — OLD is NOT restored, because a network throw does not prove
    // the cookie was left unchanged.
    expect(storedEpoch()).not.toBe('epoch-old')
    expect(storedEpoch()).toBe(precommitted)
  })

  // Test 1 (section 2) — SUCCESS status then JSON parse failure: KEEP NEXT.
  it('KEEPS NEXT (no rollback) on a 200 whose JSON parsing fails', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-old')
    // 200 with a body that is not valid JSON → response.json() throws.
    vi.stubGlobal('fetch', vi.fn(async () => new Response('<<not json>>', {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })))

    const result = await login({ email: 'u', password: 'p' })

    expect(result.kind).toBe('unavailable')
    // Set-Cookie was processed on the 200 → NEXT is committed; OLD must NOT
    // be restored (would strand cookie=NEW / epoch=OLD).
    expect(storedEpoch()).not.toBe('epoch-old')
    expect(storedEpoch()).not.toBeNull()
  })

  // Test 6 (section 2) — rollback failure on a rollback-ELIGIBLE path (401):
  // OLD must not be falsely advertised; outcome is unavailable.
  it('fails closed (unavailable) when rollback fails on a rollback-eligible 401', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-old')
    // Allow the precommit write (call #1) but throw on the rollback (call #2).
    let n = 0
    const realSet = Storage.prototype.setItem
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (
      this: Storage,
      key: string,
      value: string,
    ) {
      n += 1
      if (n >= 2) throw new DOMException('denied')
      return realSet.call(this, key, value)
    })
    const fetchHarness = stubFetch()
    fetchHarness.setNext(new Response(null, { status: 401 }))

    const result = await login({ email: 'x', password: 'y' })

    // A 401 is rollback-eligible, but the rollback write failed → we must NOT
    // return invalid-credentials (which would imply OLD is intact). Fail closed.
    expect(result.kind).toBe('unavailable')
    expect(storedEpoch()).not.toBe('epoch-old')
    expect(storedEpoch()).not.toBeNull()
  })
})

describe('authApi.logout — precommit protocol', () => {
  // Test J: successful logout keeps NEXT.
  it('keeps NEXT on a successful (204) logout', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-old')
    const okFetch = stubFetch()
    okFetch.setNext(new Response(null, { status: 204 }))

    const ok = await logout()

    expect(ok).toEqual({ kind: 'success' })
    expect(storedEpoch()).not.toBe('epoch-old')
  })

  // Test 4 (section 2) — AMBIGUOUS transport failure: KEEP NEXT, fail closed.
  // Logout has no deliberate non-mutating failure status, so it NEVER rolls
  // back after dispatch.
  it('KEEPS NEXT (no rollback) on an ambiguous network failure after dispatch', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-old')
    let precommitted: string | null = null
    const realSet = Storage.prototype.setItem
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (
      this: Storage,
      key: string,
      value: string,
    ) {
      if (key === SESSION_EPOCH_STORAGE_KEY) precommitted = value
      return realSet.call(this, key, value)
    })
    vi.stubGlobal('fetch', vi.fn(async () => {
      throw new TypeError('network down')
    }))

    const result = await logout()

    expect(result.kind).toBe('unavailable')
    expect(storedEpoch()).not.toBe('epoch-old')
    expect(storedEpoch()).toBe(precommitted)
  })
})

// ────────────────────────────────────────────────────────────────────────
// authApi.logout — session epoch rotation
// ────────────────────────────────────────────────────────────────────────

describe('authApi.logout — session epoch rotation', () => {
  it('rotates the shared epoch to a NEW value on a successful logout', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-before')
    const fetchHarness = stubFetch()
    fetchHarness.setNext(new Response(null, { status: 204 }))

    const result = await logout()

    expect(result).toEqual({ kind: 'success' })
    const after = storedEpoch()
    expect(after).not.toBeNull()
    expect(after).not.toBe('epoch-before')
  })

  it('treats 200 as unavailable, not successful logout (only 204 is confirmed)', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-old')
    const fetchHarness = stubFetch()
    fetchHarness.setNext(new Response(null, { status: 200 }))

    const result = await logout()

    expect(result.kind).toBe('unavailable')
    expect(result.kind).not.toBe('success')
    // NEXT remains committed — OLD is NOT restored.
    expect(storedEpoch()).not.toBe('epoch-old')
    expect(storedEpoch()).not.toBeNull()
    // Exactly one logout POST occurred.
    expect(fetchHarness.calls).toHaveLength(1)
    expect(fetchHarness.calls[0]!.input).toBe(AUTH_LOGOUT_PATH)
  })

  it('KEEPS NEXT (fails closed) when logout returns an unexpected status (no rollback-eligible failure)', async () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-before')
    const fetchHarness = stubFetch()
    fetchHarness.setNext(new Response(null, { status: 500 }))

    const result = await logout()

    // A 500 is ambiguous — logout keeps NEXT rather than restoring OLD.
    expect(result.kind).toBe('unavailable')
    expect(storedEpoch()).not.toBe('epoch-before')
  })

  it('fails closed with unavailable and ZERO POST /logout when epoch storage is unavailable', async () => {
    const fetchHarness = stubFetch()
    fetchHarness.setNext(new Response(null, { status: 204 }))
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('QuotaExceededError')
    })

    const result = await logout()

    expect(result.kind).toBe('unavailable')
    expect(fetchHarness.calls).toHaveLength(0)
  })
})
