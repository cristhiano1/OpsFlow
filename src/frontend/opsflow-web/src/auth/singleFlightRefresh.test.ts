import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { AUTH_REFRESH_PATH } from './authApi'
import type { LoginResponse } from './contracts'
import {
  _resetSessionStoreForTests,
  currentGeneration,
  getAccessToken,
  invalidateSession,
  setAccessToken,
} from './sessionStore'
import { _resetSingleFlightForTests, refreshOnce } from './singleFlightRefresh'

// ────────────────────────────────────────────────────────────────────────
// Test helpers
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

function jsonBody(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function sampleLogin(token: string): LoginResponse {
  return {
    accessToken: token,
    accessTokenExpiresAt: '2030-01-01T00:00:00.000+00:00',
    user: {
      userId: '11111111-1111-1111-1111-111111111111',
      email: 'user@test.local',
      displayName: 'Test User',
      organizationId: '22222222-2222-2222-2222-222222222222',
      organizationName: 'Test Org',
      roles: ['Coordinator'],
    },
  }
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
  // When set, `request()` awaits this before invoking the callback — used to
  // simulate another tab currently holding the lock.
  acquireGate?: Deferred<void>
  // When set, `request()` throws with this error before invoking any callback.
  throwOnAcquire?: unknown
}

function makeFakeLockManager(): FakeLockHarness {
  const harness: FakeLockHarness = {
    // Assigned below to close over `harness`.
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
  Object.defineProperty(navigator, 'locks', {
    configurable: true,
    value: manager,
  })
}

function uninstallLocks(): void {
  Object.defineProperty(navigator, 'locks', {
    configurable: true,
    value: undefined,
  })
}

// ────────────────────────────────────────────────────────────────────────
// Suite lifecycle: default = a passthrough fake LockManager is installed so
// existing tests exercise the "Web Locks available" happy path. Tests that
// need the unavailable / throwing path uninstall or replace it explicitly.
// ────────────────────────────────────────────────────────────────────────

let defaultLockHarness: FakeLockHarness | null = null

beforeEach(() => {
  _resetSessionStoreForTests()
  _resetSingleFlightForTests()
  defaultLockHarness = makeFakeLockManager()
  installFakeLocks(defaultLockHarness.manager)
})

afterEach(() => {
  vi.unstubAllGlobals()
  uninstallLocks()
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

    const p1 = refreshOnce()
    const p2 = refreshOnce()
    const p3 = refreshOnce()

    // All three must share the same underlying promise instance.
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
    // Exactly one lock acquisition regardless of concurrent callers.
    expect(defaultLockHarness!.requestCalls).toHaveLength(1)
    expect(defaultLockHarness!.requestCalls[0]).toEqual({ name: 'opsflow.auth.refresh' })
  })

  it('clears the pending slot on success so a later call issues a fresh refresh', async () => {
    const harness = stubFetch()
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-a'), 200)))
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-b'), 200)))

    await refreshOnce()
    await refreshOnce()

    expect(harness.calls).toHaveLength(2)
    expect(getAccessToken()).toBe('t-b')
  })

  it('clears the pending slot on failure so a later call issues a fresh refresh', async () => {
    const harness = stubFetch()
    harness.queue.push(() => Promise.resolve(new Response(null, { status: 401 })))
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-recovered'), 200)))

    const first = await refreshOnce()
    expect(first.kind).toBe('unauthenticated')

    const second = await refreshOnce()
    expect(second.kind).toBe('refreshed')

    expect(harness.calls).toHaveLength(2)
    expect(getAccessToken()).toBe('t-recovered')
  })

  it('never restores the token when the session was invalidated while the refresh was in flight', async () => {
    const harness = stubFetch()
    const gate = defer<Response>()
    harness.queue.push(() => gate.promise)

    // Give the store an initial token so we can prove it was NOT overwritten.
    setAccessToken('pre-existing', currentGeneration())

    const inFlight = refreshOnce()

    // Simulate logout / terminal auth failure while refresh is on the wire.
    invalidateSession()

    // Refresh returns a fresh token AFTER invalidation.
    gate.resolve(jsonBody(sampleLogin('stale-token'), 200))
    const result = await inFlight

    expect(result.kind).toBe('stale')
    expect(getAccessToken()).toBeNull()
  })

  it('returns unavailable and clears pending when refresh throws', async () => {
    const harness = stubFetch()
    harness.queue.push(() => Promise.reject(new TypeError('offline')))
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-2'), 200)))

    const first = await refreshOnce()
    expect(first.kind).toBe('unavailable')

    const second = await refreshOnce()
    expect(second.kind).toBe('refreshed')
    expect(getAccessToken()).toBe('t-2')
  })
})

// ────────────────────────────────────────────────────────────────────────
// Cross-tab Web Locks coordination
// ────────────────────────────────────────────────────────────────────────

describe('singleFlightRefresh — cross-tab Web Locks coordination', () => {
  it('acquires the fixed exclusive lock name before POST /refresh, and the network refresh happens INSIDE the lock callback', async () => {
    const harness = stubFetch()
    // Refresh returns immediately once fetch is invoked — no gate needed on
    // the response because ordering is proved deterministically by "no
    // fetch before the lock releases; fetch has occurred after refreshOnce
    // resolves".
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-new'), 200)))

    // Replace the default with a gated lock so we can observe strict ordering.
    uninstallLocks()
    const gatedLock = makeFakeLockManager()
    gatedLock.acquireGate = defer<void>()
    installFakeLocks(gatedLock.manager)

    const promise = refreshOnce()

    // The lock is requested immediately with the fixed name; the request is
    // now suspended waiting for acquireGate. Because the lock's callback has
    // not yet been invoked, /refresh has NOT been called.
    expect(gatedLock.requestCalls).toEqual([{ name: 'opsflow.auth.refresh' }])
    expect(harness.calls).toHaveLength(0)

    // Release the lock and await the whole refresh cycle. Awaiting the
    // returned promise is a deterministic synchronization point: the
    // promise cannot resolve without the callback having run to completion,
    // which is the only path that can invoke fetch.
    gatedLock.acquireGate.resolve()
    const result = await promise

    // Ordering proof: fetch count was 0 before releasing the lock and is
    // now exactly 1 for /refresh. The only code path that could have
    // called fetch in that window is the lock's callback.
    expect(harness.calls).toEqual([AUTH_REFRESH_PATH])
    expect(result.kind).toBe('refreshed')
    expect(getAccessToken()).toBe('t-new')
  })

  it('returns stale WITHOUT calling POST /refresh when the session is invalidated while WAITING for the cross-tab lock', async () => {
    const harness = stubFetch()
    // No fetch responses queued — we assert zero fetches occur.

    uninstallLocks()
    const gatedLock = makeFakeLockManager()
    gatedLock.acquireGate = defer<void>()
    installFakeLocks(gatedLock.manager)

    setAccessToken('pre-existing', currentGeneration())

    const promise = refreshOnce()

    // The lock request has been issued but is waiting on the gate.
    expect(gatedLock.requestCalls).toHaveLength(1)

    // Simulate a LOCAL logout in this tab while it is still waiting for
    // the cross-tab lock (which a sibling tab is holding). Only the local
    // logout bumps THIS tab's generation; the sibling's refresh does not.
    invalidateSession()

    // Release the lock — the callback runs, re-checks generation, and
    // returns stale WITHOUT calling authRefresh.
    gatedLock.acquireGate.resolve()
    const result = await promise

    expect(result.kind).toBe('stale')
    expect(harness.calls).toHaveLength(0) // zero POST /refresh
    expect(getAccessToken()).toBeNull()
  })
})

// ────────────────────────────────────────────────────────────────────────
// Fail-closed availability policy
// ────────────────────────────────────────────────────────────────────────

describe('singleFlightRefresh — fail-closed when Web Locks is unavailable', () => {
  it('returns unavailable and issues ZERO POST /refresh when navigator.locks is missing', async () => {
    uninstallLocks()
    const harness = stubFetch()
    setAccessToken('pre-existing', currentGeneration())
    const initialGeneration = currentGeneration()

    const result = await refreshOnce()

    expect(result.kind).toBe('unavailable')
    expect(harness.calls).toHaveLength(0)
    // Session state untouched — no token clear, no generation bump.
    expect(getAccessToken()).toBe('pre-existing')
    expect(currentGeneration()).toBe(initialGeneration)
  })

  it('returns unavailable and issues ZERO POST /refresh when lock acquisition throws', async () => {
    uninstallLocks()
    const throwingLock = makeFakeLockManager()
    throwingLock.throwOnAcquire = new Error('lock rejected')
    installFakeLocks(throwingLock.manager)

    const harness = stubFetch()
    setAccessToken('pre-existing', currentGeneration())
    const initialGeneration = currentGeneration()

    const result = await refreshOnce()

    expect(result.kind).toBe('unavailable')
    if (result.kind === 'unavailable') {
      expect((result.error as Error).message).toBe('lock rejected')
    }
    expect(harness.calls).toHaveLength(0)
    expect(getAccessToken()).toBe('pre-existing')
    expect(currentGeneration()).toBe(initialGeneration)
  })

  it('clears the pending slot after an unavailable outcome so a later call can retry when the runtime recovers', async () => {
    uninstallLocks()
    const harness = stubFetch()

    const first = await refreshOnce()
    expect(first.kind).toBe('unavailable')

    // Restore Web Locks — a later refresh cycle must be able to run.
    const recovered = makeFakeLockManager()
    installFakeLocks(recovered.manager)
    harness.queue.push(() => Promise.resolve(jsonBody(sampleLogin('t-recovered'), 200)))

    const second = await refreshOnce()
    expect(second.kind).toBe('refreshed')
    expect(getAccessToken()).toBe('t-recovered')
    expect(recovered.requestCalls).toHaveLength(1)
  })
})
