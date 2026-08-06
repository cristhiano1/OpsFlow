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

afterEach(() => {
  vi.unstubAllGlobals()
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
