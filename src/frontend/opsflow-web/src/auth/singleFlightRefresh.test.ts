import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { AUTH_REFRESH_PATH } from './authApi'
import { AUTH_COOKIE_LOCK_NAME } from './authCookieLock'
import type { LoginResponse } from './contracts'
import {
  type ExpectedEpoch,
  SESSION_EPOCH_STORAGE_KEY,
  _resetSessionEpochForTests,
} from './sessionEpoch'
import {
  _resetSessionStoreForTests,
  currentGeneration,
  getAccessToken,
  getBoundEpoch,
  getPrincipal,
  invalidateSession,
  type Principal,
  setSession,
} from './sessionStore'
import { _resetSingleFlightForTests, refreshOnce } from './singleFlightRefresh'

// ────────────────────────────────────────────────────────────────────────
// Test helpers
// ────────────────────────────────────────────────────────────────────────

const EPOCH_A = 'epoch-A'
const EPOCH_B = 'epoch-B'

// Principal that matches the user embedded in `sampleLogin`.
const PRINCIPAL_A: Principal = {
  userId: '11111111-1111-1111-1111-111111111111',
  organizationId: '22222222-2222-2222-2222-222222222222',
}
const PRINCIPAL_B: Principal = {
  userId: '99999999-9999-9999-9999-999999999999',
  organizationId: '88888888-8888-8888-8888-888888888888',
}
const PRINCIPAL_C: Principal = {
  userId: '33333333-3333-3333-3333-333333333333',
  organizationId: '77777777-7777-7777-7777-777777777777',
}
const EPOCH_C = 'epoch-C'

const present = (epoch: string): ExpectedEpoch => ({ kind: 'present', epoch })
const MISSING: ExpectedEpoch = { kind: 'missing' }

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

function sampleLoginFor(token: string, p: Principal): LoginResponse {
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
  return sampleLoginFor(token, PRINCIPAL_A)
}

interface FetchHarness {
  calls: RequestInfo[]
  queue: Array<() => Promise<Response>>
}

function stubFetch(): FetchHarness {
  const harness: FetchHarness = { calls: [], queue: [] }
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      harness.calls.push(input as RequestInfo)
      const next = harness.queue.shift()
      if (next === undefined) {
        throw new Error(`fetch called with no queued response for ${String(input)}`)
      }
      return next()
    }),
  )
  return harness
}

// ────────────────────────────────────────────────────────────────────────
// Fake Web Locks
// ────────────────────────────────────────────────────────────────────────

type LockCallback<T> = (lock: unknown) => Promise<T> | T

interface FakeLockManager {
  request<T>(name: string, callback: LockCallback<T>): Promise<T>
  request<T>(name: string, options: unknown, callback: LockCallback<T>): Promise<T>
}

interface FakeLockHarness {
  manager: FakeLockManager
  requestCalls: Array<{ name: string }>
  acquireGate?: Deferred<void>
  throwOnAcquire?: unknown
}

function makeFakeLockManager(): FakeLockHarness {
  const harness: FakeLockHarness = {
    manager: undefined as unknown as FakeLockManager,
    requestCalls: [],
  }
  harness.manager = {
    request: (async (name: string, ...args: unknown[]): Promise<unknown> => {
      const callback = (typeof args[0] === 'function' ? args[0] : args[1]) as LockCallback<unknown>
      harness.requestCalls.push({ name })
      if (harness.throwOnAcquire !== undefined) {
        throw harness.throwOnAcquire
      }
      if (harness.acquireGate !== undefined) {
        await harness.acquireGate.promise
      }
      return await callback({ name })
    }) as FakeLockManager['request'],
  }
  return harness
}

function installFakeLocks(manager: FakeLockManager): void {
  Object.defineProperty(navigator, 'locks', { configurable: true, value: manager })
}

function uninstallLocks(): void {
  Object.defineProperty(navigator, 'locks', { configurable: true, value: undefined })
}

// ────────────────────────────────────────────────────────────────────────
// Suite lifecycle: default = passthrough fake LockManager + an established
// shared epoch (EPOCH_A) in real jsdom localStorage.
// ────────────────────────────────────────────────────────────────────────

let defaultLockHarness: FakeLockHarness | null = null

beforeEach(() => {
  _resetSessionStoreForTests()
  _resetSingleFlightForTests()
  _resetSessionEpochForTests()
  localStorage.clear()
  localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_A)
  defaultLockHarness = makeFakeLockManager()
  installFakeLocks(defaultLockHarness.manager)
})

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  uninstallLocks()
  localStorage.clear()
  defaultLockHarness = null
})

// ────────────────────────────────────────────────────────────────────────
// In-tab single-flight (existing behavior — preserved)
// ────────────────────────────────────────────────────────────────────────

describe('singleFlightRefresh — in-tab single-flight', () => {
  it('makes exactly one POST /refresh when multiple callers invoke it concurrently', async () => {
    const harness = stubFetch()
    const gate = defer<Response>()
    harness.queue.push(() => gate.promise)

    const p1 = refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    const p2 = refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    const p3 = refreshOnce(present(EPOCH_A), PRINCIPAL_A)

    expect(p1).toBe(p2)
    expect(p2).toBe(p3)

    gate.resolve(jsonBody(sampleLogin('t-1'), 200))
    const [r1, r2, r3] = await Promise.all([p1, p2, p3])

    expect(harness.calls).toHaveLength(1)
    expect(harness.calls[0]).toBe(AUTH_REFRESH_PATH)
    expect(r1.kind).toBe('refreshed')
    expect(r2).toEqual(r1)
    expect(r3).toEqual(r1)
    expect(getAccessToken()).toBe('t-1')
    expect(getPrincipal()).toEqual(PRINCIPAL_A)
    // Test D: a normal refresh installs the new token bound to the SAME epoch.
    expect(getBoundEpoch()).toBe(EPOCH_A)
    // Exactly one lock acquisition, using the SHARED constant — proving
    // refresh serialises through the same mutex as login and logout.
    expect(defaultLockHarness!.requestCalls).toHaveLength(1)
    expect(defaultLockHarness!.requestCalls[0]).toEqual({ name: AUTH_COOKIE_LOCK_NAME })
  })

  it('clears the pending slot on success so a later call issues a fresh refresh', async () => {
    const harness = stubFetch()
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-a'), 200)))
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-b'), 200)))

    await refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    await refreshOnce(present(EPOCH_A), PRINCIPAL_A)

    expect(harness.calls).toHaveLength(2)
    expect(getAccessToken()).toBe('t-b')
  })

  it('clears the pending slot on failure so a later call issues a fresh refresh', async () => {
    const harness = stubFetch()
    harness.queue.push(() => Promise.resolve(new Response(null, { status: 401 })))
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-recovered'), 200)))

    const first = await refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    expect(first.kind).toBe('unauthenticated')

    const second = await refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    expect(second.kind).toBe('refreshed')

    expect(harness.calls).toHaveLength(2)
    expect(getAccessToken()).toBe('t-recovered')
  })

  it('never restores the token when the session was invalidated (LOCAL) while the refresh was in flight', async () => {
    const harness = stubFetch()
    const gate = defer<Response>()
    harness.queue.push(() => gate.promise)

    setSession('pre-existing', PRINCIPAL_A, EPOCH_A, currentGeneration())

    const inFlight = refreshOnce(present(EPOCH_A), PRINCIPAL_A)

    // Local logout while the refresh is on the wire.
    invalidateSession()

    gate.resolve(jsonBody(sampleLogin('stale-token'), 200))
    const result = await inFlight

    expect(result.kind).toBe('stale')
    expect(getAccessToken()).toBeNull()
  })

  it('returns unavailable and clears pending when refresh throws', async () => {
    const harness = stubFetch()
    harness.queue.push(() => Promise.reject(new TypeError('offline')))
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-2'), 200)))

    const first = await refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    expect(first.kind).toBe('unavailable')

    const second = await refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    expect(second.kind).toBe('refreshed')
    expect(getAccessToken()).toBe('t-2')
  })

  it('cold bootstrap: refreshOnce(MISSING, null) establishes the epoch and installs the session', async () => {
    localStorage.clear() // no epoch yet
    const harness = stubFetch()
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-boot'), 200)))

    const result = await refreshOnce(MISSING, null)

    expect(result.kind).toBe('refreshed')
    expect(getAccessToken()).toBe('t-boot')
    // Test E: cold bootstrap installs token + principal + established epoch
    // atomically — the token's bound epoch equals the newly established
    // shared epoch.
    const established = localStorage.getItem(SESSION_EPOCH_STORAGE_KEY)
    expect(established).not.toBeNull()
    expect(getBoundEpoch()).toBe(established)
    expect(getPrincipal()).toEqual(PRINCIPAL_A)
  })
})

// ────────────────────────────────────────────────────────────────────────
// Cross-tab Web Locks coordination (ordering)
// ────────────────────────────────────────────────────────────────────────

describe('singleFlightRefresh — cross-tab Web Locks coordination', () => {
  it('acquires the fixed exclusive lock name before POST /refresh, and the network refresh happens INSIDE the lock callback', async () => {
    const harness = stubFetch()
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-new'), 200)))

    uninstallLocks()
    const gatedLock = makeFakeLockManager()
    gatedLock.acquireGate = defer<void>()
    installFakeLocks(gatedLock.manager)

    const promise = refreshOnce(present(EPOCH_A), PRINCIPAL_A)

    expect(gatedLock.requestCalls).toEqual([{ name: AUTH_COOKIE_LOCK_NAME }])
    expect(harness.calls).toHaveLength(0)

    gatedLock.acquireGate.resolve()
    const result = await promise

    expect(harness.calls).toEqual([AUTH_REFRESH_PATH])
    expect(result.kind).toBe('refreshed')
    expect(getAccessToken()).toBe('t-new')
  })

  it('returns stale WITHOUT calling POST /refresh when the LOCAL session is invalidated while WAITING for the lock', async () => {
    const harness = stubFetch()

    uninstallLocks()
    const gatedLock = makeFakeLockManager()
    gatedLock.acquireGate = defer<void>()
    installFakeLocks(gatedLock.manager)

    setSession('pre-existing', PRINCIPAL_A, EPOCH_A, currentGeneration())

    const promise = refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    expect(gatedLock.requestCalls).toHaveLength(1)

    // LOCAL logout while waiting for the lock — bumps this tab's generation.
    invalidateSession()

    gatedLock.acquireGate.resolve()
    const result = await promise

    expect(result.kind).toBe('stale')
    expect(harness.calls).toHaveLength(0)
    expect(getAccessToken()).toBeNull()
  })
})

// ────────────────────────────────────────────────────────────────────────
// Cross-tab session replacement (epoch guard) — the P1
// ────────────────────────────────────────────────────────────────────────

describe('singleFlightRefresh — cross-tab session-replacement (epoch) guard', () => {
  it('returns session-replaced with ZERO POST when a sibling rotates the epoch while WAITING for the lock', async () => {
    const harness = stubFetch()
    // No fetch responses queued — we assert zero POST /refresh.

    uninstallLocks()
    const gatedLock = makeFakeLockManager()
    gatedLock.acquireGate = defer<void>()
    installFakeLocks(gatedLock.manager)

    setSession('token-A', PRINCIPAL_A, EPOCH_A, currentGeneration())

    // Request captured epoch A and is now queued behind the lock.
    const promise = refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    expect(gatedLock.requestCalls).toHaveLength(1)

    // Sibling tab logs in as B and rotates the shared epoch while we wait.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)

    // We finally acquire the lock — the post-lock epoch re-check fires.
    gatedLock.acquireGate.resolve()
    const result = await promise

    expect(result.kind).toBe('session-replaced')
    expect(harness.calls).toHaveLength(0) // NO POST /refresh
    // The old token/principal are untouched (no B token installed here).
    expect(getAccessToken()).toBe('token-A')
    expect(getPrincipal()).toEqual(PRINCIPAL_A)
  })

  it('returns session-replaced with ZERO POST when the epoch already differs before the POST', async () => {
    const harness = stubFetch()
    setSession('token-A', PRINCIPAL_A, EPOCH_A, currentGeneration())

    // Sibling already replaced the session before we even queue.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)

    const result = await refreshOnce(present(EPOCH_A), PRINCIPAL_A)

    expect(result.kind).toBe('session-replaced')
    expect(harness.calls).toHaveLength(0)
    expect(getAccessToken()).toBe('token-A')
  })

  it('does NOT install a foreign token/principal: principal mismatch → session-replaced BEFORE setSession (epoch unchanged)', async () => {
    const PRINCIPAL_B: Principal = {
      userId: '99999999-9999-9999-9999-999999999999',
      organizationId: '88888888-8888-8888-8888-888888888888',
    }
    const harness = stubFetch()
    setSession('token-A', PRINCIPAL_A, EPOCH_A, currentGeneration())

    // Epoch stays A the whole time. The refresh response, however, belongs to
    // account B (synthetic — a real account switch would rotate the epoch).
    harness.queue.push(() => Promise.resolve(jsonBody({
      accessToken: 'token-B',
      accessTokenExpiresAt: '2030-01-01T00:00:00.000+00:00',
      user: {
        userId: PRINCIPAL_B.userId,
        email: 'other@test.local',
        displayName: 'Other',
        organizationId: PRINCIPAL_B.organizationId,
        organizationName: 'Other Org',
        roles: ['Viewer'],
      },
    }, 200)))

    // Expected principal is A.
    const result = await refreshOnce(present(EPOCH_A), PRINCIPAL_A)

    expect(result.kind).toBe('session-replaced')
    // POST /refresh DID happen (epoch matched), but the mismatched token was
    // rejected BEFORE installation — token B / principal B are NOT in the store.
    expect(harness.calls).toHaveLength(1)
    expect(getAccessToken()).toBe('token-A')
    expect(getPrincipal()).toEqual(PRINCIPAL_A)
  })

  it('cold bootstrap (expectedPrincipal = null) installs whatever principal the refresh returns', async () => {
    localStorage.clear() // no epoch yet
    _resetSessionStoreForTests()
    const harness = stubFetch()
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-boot'), 200)))

    const result = await refreshOnce(MISSING, null)

    expect(result.kind).toBe('refreshed')
    expect(getAccessToken()).toBe('t-boot')
    expect(getPrincipal()).toEqual(PRINCIPAL_A)
  })
})

// ────────────────────────────────────────────────────────────────────────
// ExpectedEpoch missing→present — the P1 fix. A caller that captured
// "no epoch" must NEVER adopt an epoch that a sibling created in between.
// ────────────────────────────────────────────────────────────────────────

describe('singleFlightRefresh — MISSING expectation cannot adopt a sibling-created epoch', () => {
  // P1 EXACT REPRODUCTION — the sequence described in the review.
  it('MISSING at capture → sibling creates E_B before we get the lock → session-replaced, ZERO POST', async () => {
    localStorage.clear() // shared epoch MISSING at capture

    const harness = stubFetch()
    // NO refresh response queued: a POST would throw. Proves zero POST.

    // Sibling created shared epoch E_B before this refresh acquires the lock.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)

    const result = await refreshOnce(MISSING, null)

    expect(result.kind).toBe('session-replaced')
    expect(harness.calls).toHaveLength(0)
    // No B session was ever installed into this tab.
    expect(getAccessToken()).toBeNull()
    expect(getPrincipal()).toBeNull()
    expect(getBoundEpoch()).toBeNull()
    // The shared epoch itself is left alone (we do not mutate B's session).
    expect(localStorage.getItem(SESSION_EPOCH_STORAGE_KEY)).toBe(EPOCH_B)
  })

  // Epoch created WHILE WAITING for the lock — deterministic via a gated lock.
  it('MISSING at capture → epoch appears WHILE WAITING for the lock → session-replaced, ZERO POST', async () => {
    localStorage.clear()
    const harness = stubFetch()
    // No responses queued — must not POST.

    uninstallLocks()
    const gatedLock = makeFakeLockManager()
    gatedLock.acquireGate = defer<void>()
    installFakeLocks(gatedLock.manager)

    const promise = refreshOnce(MISSING, null)
    // The lock was requested; the callback is suspended on the gate.
    expect(gatedLock.requestCalls).toHaveLength(1)

    // While we wait, a sibling successfully creates E_B.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)

    gatedLock.acquireGate.resolve()
    const result = await promise

    expect(result.kind).toBe('session-replaced')
    expect(harness.calls).toHaveLength(0)
    expect(getAccessToken()).toBeNull()
    expect(getBoundEpoch()).toBeNull()
    expect(localStorage.getItem(SESSION_EPOCH_STORAGE_KEY)).toBe(EPOCH_B)
  })

  // Legitimate first cold bootstrap: still missing under the lock → establish.
  it('MISSING at capture → STILL missing under the lock → establishes epoch, one POST, bound to established epoch', async () => {
    localStorage.clear()
    const harness = stubFetch()
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-boot'), 200)))

    const result = await refreshOnce(MISSING, null)

    expect(result.kind).toBe('refreshed')
    expect(harness.calls).toHaveLength(1)
    const established = localStorage.getItem(SESSION_EPOCH_STORAGE_KEY)
    expect(established).not.toBeNull()
    // Atomic install: token + principal + bound epoch (= newly established).
    expect(getAccessToken()).toBe('t-boot')
    expect(getPrincipal()).toEqual(PRINCIPAL_A)
    expect(getBoundEpoch()).toBe(established)
  })

  // Normal page reload with an already-existing epoch — legitimate present-E.
  it('PRESENT-E at capture with no local token → refreshes under E and installs bound-to-E', async () => {
    // Shared epoch already exists (a prior session's marker); no local state.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_A)
    const harness = stubFetch()
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-reload'), 200)))

    const result = await refreshOnce(present(EPOCH_A), null)

    expect(result.kind).toBe('refreshed')
    expect(harness.calls).toHaveLength(1)
    expect(getAccessToken()).toBe('t-reload')
    expect(getPrincipal()).toEqual(PRINCIPAL_A)
    // Bound to the SAME pre-existing epoch — not a rotation.
    expect(getBoundEpoch()).toBe(EPOCH_A)
    expect(localStorage.getItem(SESSION_EPOCH_STORAGE_KEY)).toBe(EPOCH_A)
  })
})

// ────────────────────────────────────────────────────────────────────────
// Fail-closed availability policy (Web Locks + epoch storage)
// ────────────────────────────────────────────────────────────────────────

describe('singleFlightRefresh — fail-closed availability', () => {
  it('returns unavailable and issues ZERO POST /refresh when navigator.locks is missing', async () => {
    uninstallLocks()
    const harness = stubFetch()
    setSession('pre-existing', PRINCIPAL_A, EPOCH_A, currentGeneration())
    const initialGeneration = currentGeneration()

    const result = await refreshOnce(present(EPOCH_A), PRINCIPAL_A)

    expect(result.kind).toBe('unavailable')
    expect(harness.calls).toHaveLength(0)
    expect(getAccessToken()).toBe('pre-existing')
    expect(currentGeneration()).toBe(initialGeneration)
  })

  it('returns unavailable and issues ZERO POST /refresh when lock acquisition throws', async () => {
    uninstallLocks()
    const throwingLock = makeFakeLockManager()
    throwingLock.throwOnAcquire = new Error('lock rejected')
    installFakeLocks(throwingLock.manager)

    const harness = stubFetch()
    setSession('pre-existing', PRINCIPAL_A, EPOCH_A, currentGeneration())

    const result = await refreshOnce(present(EPOCH_A), PRINCIPAL_A)

    expect(result.kind).toBe('unavailable')
    if (result.kind === 'unavailable') {
      expect((result.error as Error).message).toBe('lock rejected')
    }
    expect(harness.calls).toHaveLength(0)
    expect(getAccessToken()).toBe('pre-existing')
  })

  it('returns unavailable and issues ZERO POST /refresh when the shared epoch cannot be READ', async () => {
    const harness = stubFetch()
    setSession('pre-existing', PRINCIPAL_A, EPOCH_A, currentGeneration())

    // Break epoch READ: the post-lock epoch check cannot evaluate the
    // cross-tab invariant, so refresh fails closed (never a false 'present').
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('denied')
    })

    const result = await refreshOnce(present(EPOCH_A), PRINCIPAL_A)

    expect(result.kind).toBe('unavailable')
    expect(harness.calls).toHaveLength(0)
    expect(getAccessToken()).toBe('pre-existing')
  })

  it('clears the pending slot after an unavailable outcome so a later call can retry when the runtime recovers', async () => {
    uninstallLocks()
    const harness = stubFetch()

    const first = await refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    expect(first.kind).toBe('unavailable')

    const recovered = makeFakeLockManager()
    installFakeLocks(recovered.manager)
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-recovered'), 200)))

    const second = await refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    expect(second.kind).toBe('refreshed')
    expect(getAccessToken()).toBe('t-recovered')
    expect(recovered.requestCalls).toHaveLength(1)
  })
})

// ────────────────────────────────────────────────────────────────────────
// Parameter-aware single-flight (identity-keyed pending)
// ────────────────────────────────────────────────────────────────────────

describe('singleFlightRefresh — parameter-aware single-flight', () => {
  // Test A: same epoch + same principal → same promise / one POST.
  it('shares one Promise and one POST for the SAME logical session', async () => {
    const harness = stubFetch()
    const gate = defer<Response>()
    harness.queue.push(() => gate.promise)

    const p1 = refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    const p2 = refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    expect(p1).toBe(p2)

    gate.resolve(jsonBody(sampleLogin('t-1'), 200))
    await Promise.all([p1, p2])
    expect(harness.calls).toHaveLength(1)
  })

  // Test B: a different epoch while an old refresh is pending must NOT consume
  // the old result; after the old settles, the new session runs its own.
  it('does NOT consume the old pending result for a different epoch; runs its own after the old settles', async () => {
    const harness = stubFetch()
    const gate1 = defer<Response>()
    harness.queue.push(() => gate1.promise) // old refresh (E1 / A)
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLoginFor('t-b', PRINCIPAL_B), 200))) // new (E2 / B)

    // Old caller for session (EPOCH_A, A); it is now gated inside authRefresh.
    const p1 = refreshOnce(present(EPOCH_A), PRINCIPAL_A)

    // A NEW local session (EPOCH_B, B). Rotate the shared epoch to match it.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)
    const p2 = refreshOnce(present(EPOCH_B), PRINCIPAL_B)
    expect(p2).not.toBe(p1)

    // Release the old refresh. Its post-response epoch check now sees EPOCH_B
    // (≠ its expected EPOCH_A) → session-replaced; it installs nothing.
    gate1.resolve(jsonBody(sampleLogin('t-a'), 200))
    const r1 = await p1
    const r2 = await p2

    expect(r1.kind).toBe('session-replaced')
    expect(r2.kind).toBe('refreshed')
    // Two distinct POSTs — the second did NOT reuse the first.
    expect(harness.calls).toHaveLength(2)
    expect(getAccessToken()).toBe('t-b')
    expect(getBoundEpoch()).toBe(EPOCH_B)
  })

  // Test C: same epoch but different principal → not shared.
  it('does NOT share the pending refresh for the SAME epoch but a DIFFERENT principal', async () => {
    const harness = stubFetch()
    const gate = defer<Response>()
    harness.queue.push(() => gate.promise) // old (A)
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-a2'), 200))) // new caller's own cycle

    const p1 = refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    const p2 = refreshOnce(present(EPOCH_A), PRINCIPAL_B) // same epoch, different principal
    expect(p2).not.toBe(p1)

    gate.resolve(jsonBody(sampleLogin('t-a'), 200))
    await p1
    const r2 = await p2

    // p2 ran its OWN cycle; the refresh returned principal A ≠ expected B →
    // session-replaced (it did not adopt p1's refreshed token).
    expect(r2.kind).toBe('session-replaced')
    expect(harness.calls).toHaveLength(2)
  })

  // Test D: after an old pending returns session-replaced, a newer valid
  // caller can start its own refresh.
  it('lets a newer valid caller start its own refresh after the old one returned session-replaced', async () => {
    const harness = stubFetch()
    // Shared epoch is already EPOCH_B, so an (EPOCH_A, A) refresh is replaced
    // immediately with zero POST.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLoginFor('t-b', PRINCIPAL_B), 200)))

    const r1 = await refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    expect(r1.kind).toBe('session-replaced')

    const r2 = await refreshOnce(present(EPOCH_B), PRINCIPAL_B)
    expect(r2.kind).toBe('refreshed')
    expect(getAccessToken()).toBe('t-b')
  })

  // Multi-waiter proof #1: two IDENTICAL mismatched waiters behind an old
  // pending must SHARE the next cycle — only one E2/B POST, and never two
  // refresh POSTs concurrently.
  it('two identical mismatched waiters behind an old pending share ONE next cycle (one E2/B POST)', async () => {
    const harness = stubFetch()
    const gateA = defer<Response>()
    // Only TWO responses are queued: A's, and ONE shared E2/B cycle. If C
    // wrongly started its own cycle, a third fetch would throw "no queued
    // response" — so this also proves there is no duplicate POST.
    harness.queue.push(() => gateA.promise)
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLoginFor('t-b', PRINCIPAL_B), 200)))

    // A is pending (E1 = EPOCH_A / principalA); it is now inside authRefresh.
    const pA = refreshOnce(present(EPOCH_A), PRINCIPAL_A)

    // A new local session (E2 = EPOCH_B / principalB). Two waiters want it.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)
    const pB = refreshOnce(present(EPOCH_B), PRINCIPAL_B)
    const pC = refreshOnce(present(EPOCH_B), PRINCIPAL_B)
    // C observed B's synchronously-installed pending entry → shares B's promise.
    expect(pC).toBe(pB)

    // Release A: its post-response epoch check sees EPOCH_B ≠ EPOCH_A →
    // session-replaced. Then the single E2/B cycle runs for BOTH B and C.
    gateA.resolve(jsonBody(sampleLogin('t-a'), 200))
    const [rA, rB, rC] = await Promise.all([pA, pB, pC])

    expect(rA.kind).toBe('session-replaced')
    expect(rB.kind).toBe('refreshed')
    expect(rC).toEqual(rB)
    // Exactly two POSTs total: A's, and ONE shared E2/B.
    expect(harness.calls).toHaveLength(2)
    expect(getAccessToken()).toBe('t-b')
    expect(getBoundEpoch()).toBe(EPOCH_B)
  })

  // Multi-waiter proof #2: three DIFFERENT mismatched waiters serialize; there
  // is NEVER more than one refresh POST in flight at a time.
  it('three DIFFERENT mismatched waiters serialize — max ONE concurrent refresh POST', async () => {
    let inFlight = 0
    let maxInFlight = 0

    interface Gated {
      started: Promise<void>
      resolve: (r: Response) => void
      handler: () => Promise<Response>
    }
    function gated(): Gated {
      const started = defer<void>()
      const gate = defer<Response>()
      return {
        started: started.promise,
        resolve: (r) => gate.resolve(r),
        handler: async () => {
          inFlight += 1
          maxInFlight = Math.max(maxInFlight, inFlight)
          started.resolve()
          try {
            return await gate.promise
          } finally {
            inFlight -= 1
          }
        },
      }
    }

    const harness = stubFetch()
    const a = gated()
    const b = gated()
    const c = gated()
    harness.queue.push(a.handler)
    harness.queue.push(b.handler)
    harness.queue.push(c.handler)

    // A runs first under shared epoch EPOCH_A.
    const pA = refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    await a.started
    expect(maxInFlight).toBe(1)

    // Queue two different-identity waiters; each chains off the LATEST pending
    // (B off A, C off B) — a strictly serial chain.
    const pB = refreshOnce(present(EPOCH_B), PRINCIPAL_B)
    const pC = refreshOnce(present(EPOCH_C), PRINCIPAL_C)

    // Move shared → E2 and release A; A is replaced, then B runs under E2.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)
    a.resolve(jsonBody(sampleLogin('t-a'), 200))
    await b.started
    expect(maxInFlight).toBe(1) // A's POST finished before B's began

    // Move shared → E3 and release B; B is replaced, then C runs under E3.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_C)
    b.resolve(jsonBody(sampleLoginFor('t-b', PRINCIPAL_B), 200))
    await c.started
    expect(maxInFlight).toBe(1)

    // Release C; C matches epoch E3 + principal C → refreshed.
    c.resolve(jsonBody(sampleLoginFor('t-c', PRINCIPAL_C), 200))
    const [rA, rB, rC] = await Promise.all([pA, pB, pC])

    expect(rA.kind).toBe('session-replaced')
    expect(rB.kind).toBe('session-replaced')
    expect(rC.kind).toBe('refreshed')
    expect(getAccessToken()).toBe('t-c')
    expect(getBoundEpoch()).toBe(EPOCH_C)
    // Three POSTs total, but never two at once.
    expect(harness.calls).toHaveLength(3)
    expect(maxInFlight).toBe(1)
  })
})

// ────────────────────────────────────────────────────────────────────────
// ExpectedEpoch identity in the single-flight key
// ────────────────────────────────────────────────────────────────────────

describe('singleFlightRefresh — ExpectedEpoch identity in the pending key', () => {
  // {missing} + {missing} → share
  it('two MISSING+MISSING callers share ONE cycle', async () => {
    localStorage.clear()
    const harness = stubFetch()
    const gate = defer<Response>()
    harness.queue.push(() => gate.promise)

    const p1 = refreshOnce(MISSING, null)
    const p2 = refreshOnce(MISSING, null)
    expect(p2).toBe(p1) // same pending entry

    gate.resolve(jsonBody(sampleLogin('t-boot'), 200))
    const [r1, r2] = await Promise.all([p1, p2])
    expect(r1.kind).toBe('refreshed')
    expect(r2).toEqual(r1)
    expect(harness.calls).toHaveLength(1)
  })

  // {present:E} + {present:E} → share (already covered by earlier test but
  // asserted here with `present(...)` explicitly for the identity matrix).
  it('two PRESENT-E+PRESENT-E callers share ONE cycle', async () => {
    const harness = stubFetch()
    const gate = defer<Response>()
    harness.queue.push(() => gate.promise)

    const p1 = refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    const p2 = refreshOnce(present(EPOCH_A), PRINCIPAL_A)
    expect(p2).toBe(p1)

    gate.resolve(jsonBody(sampleLogin('t-x'), 200))
    await Promise.all([p1, p2])
    expect(harness.calls).toHaveLength(1)
  })

  // The `refreshed` result exposes the effective epoch so apiClient can
  // verify it is still current before retrying.
  it('refreshed result includes sessionEpoch equal to the expected present epoch', async () => {
    const harness = stubFetch()
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-1'), 200)))

    const result = await refreshOnce(present(EPOCH_A), PRINCIPAL_A)

    expect(result.kind).toBe('refreshed')
    if (result.kind === 'refreshed') {
      expect(result.sessionEpoch).toBe(EPOCH_A)
    }
    expect(harness.calls).toHaveLength(1)
  })

  it('refreshed result includes sessionEpoch equal to the newly established epoch (MISSING origin)', async () => {
    localStorage.clear()
    const harness = stubFetch()
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-boot'), 200)))

    const result = await refreshOnce(MISSING, null)

    expect(result.kind).toBe('refreshed')
    if (result.kind === 'refreshed') {
      const established = localStorage.getItem(SESSION_EPOCH_STORAGE_KEY)
      expect(established).not.toBeNull()
      expect(result.sessionEpoch).toBe(established)
    }
  })

  // {missing} + {present:E} → do NOT share (they are different logical
  // sessions; the second waits for the first, then runs its own cycle).
  it('MISSING and PRESENT-E callers do NOT share the same pending entry', async () => {
    localStorage.clear()
    const harness = stubFetch()
    const gateA = defer<Response>()
    // A is a MISSING-cold-bootstrap cycle (queued first).
    harness.queue.push(() => gateA.promise)
    // B is a PRESENT-E cycle that runs AFTER A settles.
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-b'), 200)))

    const pMissing = refreshOnce(MISSING, null)
    // Sibling created E_B before B's caller enters.
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, EPOCH_B)
    const pPresent = refreshOnce(present(EPOCH_B), PRINCIPAL_A)

    // Different logical-session keys → distinct promises.
    expect(pPresent).not.toBe(pMissing)

    // Release A: MISSING sees EPOCH_B present → session-replaced (zero POST
    // for A — the "queue.push(gateA)" handler still fires, so this proves A
    // did dispatch, but MISSING's rule is that the sibling's appearance is
    // caught BEFORE the network call). To also cover that here, release the
    // gate so A's promise settles even if the handler never runs.
    gateA.resolve(jsonBody(sampleLogin('t-a'), 200))
    const [rA, rB] = await Promise.all([pMissing, pPresent])

    expect(rA.kind).toBe('session-replaced')
    expect(rB.kind).toBe('refreshed')
    // Verify strict serialization: at MOST one POST was ever in flight, and
    // the MISSING caller did NOT reuse B's cycle result.
    expect(getAccessToken()).toBe('t-b')
    expect(getBoundEpoch()).toBe(EPOCH_B)
  })
})
