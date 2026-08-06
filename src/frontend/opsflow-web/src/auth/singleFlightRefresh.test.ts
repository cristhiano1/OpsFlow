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

beforeEach(() => {
  _resetSessionStoreForTests()
  _resetSingleFlightForTests()
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('singleFlightRefresh', () => {
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
