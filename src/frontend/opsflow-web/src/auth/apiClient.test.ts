import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { apiFetch, ApiUnavailableError, type ApiRequest } from './apiClient'
import {
  AUTH_LOGIN_PATH,
  AUTH_LOGOUT_PATH,
  AUTH_ME_PATH,
  AUTH_REFRESH_PATH,
} from './authApi'
import type { LoginResponse } from './contracts'
import {
  _resetSessionStoreForTests,
  currentGeneration,
  getAccessToken,
  setAccessToken,
} from './sessionStore'
import { _resetSingleFlightForTests } from './singleFlightRefresh'

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

beforeEach(() => {
  _resetSessionStoreForTests()
  _resetSingleFlightForTests()
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('apiClient bearer injection', () => {
  it('adds Authorization: Bearer <token> when a token is present', async () => {
    setAccessToken('t-current', currentGeneration())
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
    setAccessToken('t-store', currentGeneration())
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

describe('apiClient 401 → refresh → retry', () => {
  it('retries exactly once with the refreshed token and returns the retry result', async () => {
    setAccessToken('t-old', currentGeneration())
    const harness = stubFetch()

    // First call: 401 (expired).
    harness.queue.push(async () => new Response(null, { status: 401 }))
    // Refresh call: success, new token 't-new'.
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    // Retry: 200 with the new bearer.
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
  })

  it('propagates the retry 401 without a second refresh and invalidates the session', async () => {
    setAccessToken('t-old', currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => new Response(null, { status: 401 }))

    const response = await apiFetch({ path: '/api/v1/things' })

    expect(response.status).toBe(401)
    // No third refresh must be issued.
    expect(harness.calls).toHaveLength(3)
    expect(harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)).toHaveLength(1)
    expect(getAccessToken()).toBeNull()
    expect(currentGeneration()).toBe(initialGeneration + 1)
  })

  it('propagates the original 401 without retry when refresh itself fails and invalidates the session', async () => {
    setAccessToken('t-old', currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => new Response(null, { status: 401 }))

    const response = await apiFetch({ path: '/api/v1/things' })

    expect(response.status).toBe(401)
    // First call + refresh only — NO retry attempt of /api/v1/things.
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
    setAccessToken('t-old', currentGeneration())
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))

    const response = await apiFetch({ path })

    expect(response.status).toBe(401)
    expect(harness.calls).toHaveLength(1)
    expect(harness.calls[0]!.input).toBe(path)
    // No refresh call was made for these exempt paths.
    expect(harness.calls.some((c) => c.input === AUTH_REFRESH_PATH && c !== harness.calls[0])).toBe(false)
  })
})

describe('apiClient handles /api/v1/auth/me like a normal request', () => {
  it('refreshes once and retries /me exactly once when the first /me returns 401', async () => {
    setAccessToken('t-old', currentGeneration())
    const harness = stubFetch()

    // First /me: 401 (expired access token).
    harness.queue.push(async () => new Response(null, { status: 401 }))
    // POST /refresh: success, rotated token.
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    // Retry /me: 200, current user.
    harness.queue.push(async () => jsonBody(
      {
        userId: '11111111-1111-1111-1111-111111111111',
        email: 'user@test.local',
        displayName: 'Test User',
        organizationId: '22222222-2222-2222-2222-222222222222',
        organizationName: 'Test Org',
        roles: ['Coordinator'],
      },
      200,
    ))

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
    setAccessToken('t-old', currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => new Response(null, { status: 401 }))

    const response = await apiFetch({ path: AUTH_ME_PATH })

    expect(response.status).toBe(401)
    // Exactly one refresh, exactly one retry — no third refresh cycle.
    expect(harness.calls).toHaveLength(3)
    expect(harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)).toHaveLength(1)
    expect(getAccessToken()).toBeNull()
    expect(currentGeneration()).toBe(initialGeneration + 1)
  })
})

describe('apiClient late-401 race (post-refresh)', () => {
  it('reuses the already-refreshed token for a 401 that arrives AFTER refresh completed — no second refresh', async () => {
    setAccessToken('t-old', currentGeneration())
    const harness = stubFetch()

    // Gate B's first fetch so it does not resolve until after A's whole
    // refresh-and-retry cycle has completed.
    const bFirstGate = defer<Response>()

    // Fetch order: A-first (401), B-first (gated, will resolve 401 later),
    // /refresh (200 → t-new), A-retry (200), B-retry (200 with t-new).
    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => bFirstGate.promise)
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => jsonBody({ id: 'a' }, 200))
    harness.queue.push(async () => jsonBody({ id: 'b' }, 200))

    // Both requests captured `t-old` at attempt time.
    const pA = apiFetch({ path: '/api/v1/a' })
    const pB = apiFetch({ path: '/api/v1/b' })

    // A completes fully — refresh + retry — while B's first fetch is still gated.
    const respA = await pA
    expect(respA.status).toBe(200)
    expect(getAccessToken()).toBe('t-new')

    // Now release B's original 401 — the store already contains t-new.
    bFirstGate.resolve(new Response(null, { status: 401 }))
    const respB = await pB
    expect(respB.status).toBe(200)

    // Exactly ONE POST /refresh across both flows.
    const refreshCalls = harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)
    expect(refreshCalls).toHaveLength(1)

    // Both initial requests carried the OLD bearer.
    expect((harness.calls[0]!.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-old')
    expect((harness.calls[1]!.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-old')

    // A retry and B retry both carried the NEW bearer, sourced from the store.
    expect((harness.calls[3]!.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-new')
    expect((harness.calls[4]!.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-new')

    // Five fetches total: A-first, B-first, refresh, A-retry, B-retry.
    expect(harness.calls).toHaveLength(5)
  })

  it('invalidates the session if the post-race retry (using the already-refreshed token) also returns 401', async () => {
    setAccessToken('t-old', currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()

    const bFirstGate = defer<Response>()
    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => bFirstGate.promise)
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => jsonBody({ id: 'a' }, 200))
    // B's retry with t-new is ALSO rejected — SecurityStamp rotated between
    // refresh and this retry.
    harness.queue.push(async () => new Response(null, { status: 401 }))

    const pA = apiFetch({ path: '/api/v1/a' })
    const pB = apiFetch({ path: '/api/v1/b' })

    await pA
    bFirstGate.resolve(new Response(null, { status: 401 }))
    const respB = await pB

    expect(respB.status).toBe(401)
    // Session invalidated exactly once. No second /refresh cycle.
    expect(harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)).toHaveLength(1)
    expect(getAccessToken()).toBeNull()
    expect(currentGeneration()).toBe(initialGeneration + 1)
  })
})

describe('apiClient refresh unavailability (temporary failure)', () => {
  it('REJECTS with ApiUnavailableError when refresh throws a network error; session state preserved', async () => {
    setAccessToken('t-old', currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))
    const underlyingError = new TypeError('network down')
    harness.queue.push(async () => {
      throw underlyingError
    })

    const promise = apiFetch({ path: '/api/v1/things' })

    await expect(promise).rejects.toBeInstanceOf(ApiUnavailableError)
    // Token and generation preserved — connectivity issue must not log out.
    expect(getAccessToken()).toBe('t-old')
    expect(currentGeneration()).toBe(initialGeneration)
  })

  it('carries the underlying cause on the ApiUnavailableError', async () => {
    setAccessToken('t-old', currentGeneration())
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

  it('REJECTS with ApiUnavailableError when refresh receives an unexpected server status (e.g. 500); session state preserved', async () => {
    setAccessToken('t-old', currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => new Response(null, { status: 500 }))

    await expect(apiFetch({ path: '/api/v1/things' })).rejects.toBeInstanceOf(ApiUnavailableError)
    expect(getAccessToken()).toBe('t-old')
    expect(currentGeneration()).toBe(initialGeneration)
  })

  it('lets a later apiFetch attempt a fresh refresh cycle after connectivity recovers', async () => {
    setAccessToken('t-old', currentGeneration())
    const harness = stubFetch()

    // First cycle: primary 401, refresh throws.
    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => {
      throw new TypeError('network down')
    })

    await expect(apiFetch({ path: '/api/v1/first' })).rejects.toBeInstanceOf(ApiUnavailableError)
    expect(getAccessToken()).toBe('t-old') // preserved

    // Second cycle: primary 401 again, refresh recovers, retry succeeds.
    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    const secondResponse = await apiFetch({ path: '/api/v1/second' })

    expect(secondResponse.status).toBe(200)
    expect(getAccessToken()).toBe('t-new')

    // Two POST /refresh attempts total (the failed one, then the recovered one).
    const refreshCalls = harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)
    expect(refreshCalls).toHaveLength(2)
  })
})

describe('apiClient case-insensitive Authorization stripping', () => {
  it('strips a caller-supplied lowercase `authorization` header and applies the session bearer', async () => {
    setAccessToken('t-store', currentGeneration())
    const harness = stubFetch()
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    await apiFetch({ path: '/api/v1/things', headers: { authorization: 'Bearer caller-lc' } })

    const call = harness.calls[0]!
    const outgoing = call.init!.headers as Record<string, string>
    // Lowercase caller value must NOT survive to the wire.
    expect(outgoing.authorization).toBeUndefined()
    // Canonical header is added from the session store.
    expect(outgoing.Authorization).toBe('Bearer t-store')
  })

  it('strips a caller-supplied mixed-case `AuThOrIzAtIoN` header just as reliably', async () => {
    setAccessToken('t-store', currentGeneration())
    const harness = stubFetch()
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    await apiFetch({
      path: '/api/v1/things',
      headers: { AuThOrIzAtIoN: 'Bearer caller-mc' },
    })

    const outgoing = harness.calls[0]!.init!.headers as Record<string, string>
    expect(outgoing.AuThOrIzAtIoN).toBeUndefined()
    expect(outgoing.Authorization).toBe('Bearer t-store')
  })
})

describe('apiClient terminal-401 token-identity guard', () => {
  it('does NOT invalidate the session when a newer access token has replaced the one used by the failing retry', async () => {
    setAccessToken('A', currentGeneration())
    const initialGeneration = currentGeneration()
    const harness = stubFetch()
    const retryGate = defer<Response>()

    // Cycle: primary 401 with A, refresh yields B, retry with B is gated.
    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => jsonBody(sampleLogin('B'), 200))
    harness.queue.push(async () => retryGate.promise)

    const promise = apiFetch({ path: '/api/v1/things' })

    // Let microtasks run so the refresh completes and the retry is dispatched
    // (and now suspended on retryGate).
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(getAccessToken()).toBe('B')

    // Simulate a newer refresh cycle installing token C BEFORE the gated
    // retry (still carrying B) resolves. Same generation — a successful
    // refresh does not bump generation, so token identity is the only
    // reliable guard.
    setAccessToken('C', currentGeneration())
    expect(getAccessToken()).toBe('C')
    expect(currentGeneration()).toBe(initialGeneration)

    // Retry returns 401. Terminal path MUST NOT clear C.
    retryGate.resolve(new Response(null, { status: 401 }))
    const response = await promise

    expect(response.status).toBe(401)
    // C must still be present; generation unchanged.
    expect(getAccessToken()).toBe('C')
    expect(currentGeneration()).toBe(initialGeneration)
  })
})

describe('apiClient body type contract (replayable only)', () => {
  // Compile-time regression guard: if a future edit ever widens
  // ApiRequest['body'] back to include ReadableStream, the directive on the
  // assignment below will stop matching a real type error and tsc will flag
  // it as unused, failing the build.
  it('type-check: ReadableStream is NOT assignable to ApiRequest.body', () => {
    // @ts-expect-error - ReadableStream must be excluded from ApiRequest bodies.
    const _invalid: ApiRequest = { path: '/x', body: new ReadableStream() }
    // Silence noUnusedLocals for the assertion binding.
    void _invalid
  })

  it('rejects apiFetch at runtime when a ReadableStream body slips through the type contract', async () => {
    const stream = new ReadableStream()
    await expect(
      apiFetch({
        path: '/api/v1/upload',
        method: 'POST',
        // Cast bypasses the TS guard to prove the runtime belt-and-suspenders.
        body: stream as unknown as XMLHttpRequestBodyInit,
      }),
    ).rejects.toThrow(/replayable|ReadableStream/i)
  })

  it('accepts a FormData body and replays it verbatim across a refresh retry', async () => {
    setAccessToken('t-old', currentGeneration())
    const harness = stubFetch()
    const form = new FormData()
    form.append('field', 'value')
    form.append('file', new Blob(['abc']), 'a.txt')

    // First: 401 (expired), refresh: 200 → t-new, retry: 200.
    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    const response = await apiFetch({
      path: '/api/v1/upload',
      method: 'POST',
      body: form,
    })

    expect(response.status).toBe(200)
    expect(harness.calls).toHaveLength(3)
    // The same FormData reference must be carried on both attempts —
    // FormData is inherently replayable (fetch reads it fresh each call).
    expect(harness.calls[0]!.init!.body).toBe(form)
    expect(harness.calls[2]!.input).toBe('/api/v1/upload')
    expect(harness.calls[2]!.init!.body).toBe(form)
    // Body is still enumerable after both fetch calls (not consumed).
    const entries = Array.from(form.entries())
    expect(entries).toHaveLength(2)
    // Retry carried the new bearer.
    expect((harness.calls[2]!.init!.headers as Record<string, string>).Authorization).toBe('Bearer t-new')
  })

  it('accepts a URLSearchParams body and replays it across a refresh retry', async () => {
    setAccessToken('t-old', currentGeneration())
    const harness = stubFetch()
    const params = new URLSearchParams([['a', '1'], ['b', '2']])

    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => jsonBody(sampleLogin('t-new'), 200))
    harness.queue.push(async () => jsonBody({ ok: true }, 200))

    const response = await apiFetch({
      path: '/api/v1/form',
      method: 'POST',
      body: params,
    })

    expect(response.status).toBe(200)
    expect(harness.calls[0]!.init!.body).toBe(params)
    expect(harness.calls[2]!.init!.body).toBe(params)
    // URLSearchParams is still enumerable / stringifiable after replay.
    expect(params.toString()).toBe('a=1&b=2')
  })
})

describe('apiClient concurrent 401s', () => {
  it('issues exactly one POST /refresh for many concurrent 401 responses', async () => {
    setAccessToken('t-old', currentGeneration())
    const harness = stubFetch()
    const refreshGate = defer<Response>()

    // Three concurrent primary calls, all 401.
    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => new Response(null, { status: 401 }))
    harness.queue.push(async () => new Response(null, { status: 401 }))
    // One refresh, gated so we can start retries only after it resolves.
    harness.queue.push(async () => refreshGate.promise)
    // Three retries, all 200.
    harness.queue.push(async () => jsonBody({ id: 'a' }, 200))
    harness.queue.push(async () => jsonBody({ id: 'b' }, 200))
    harness.queue.push(async () => jsonBody({ id: 'c' }, 200))

    const p1 = apiFetch({ path: '/api/v1/things/a' })
    const p2 = apiFetch({ path: '/api/v1/things/b' })
    const p3 = apiFetch({ path: '/api/v1/things/c' })

    // Let the microtask queue drain so all three see 401 and enter refresh.
    await new Promise((resolve) => setTimeout(resolve, 0))

    refreshGate.resolve(jsonBody(sampleLogin('t-new'), 200))
    const [r1, r2, r3] = await Promise.all([p1, p2, p3])

    expect(r1.status).toBe(200)
    expect(r2.status).toBe(200)
    expect(r3.status).toBe(200)

    const refreshCalls = harness.calls.filter((c) => c.input === AUTH_REFRESH_PATH)
    expect(refreshCalls).toHaveLength(1)
    expect(getAccessToken()).toBe('t-new')
  })
})
