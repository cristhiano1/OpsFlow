import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { httpRequest } from './httpClient'

interface FetchCall {
  input: RequestInfo | URL
  init: RequestInit | undefined
}

let calls: FetchCall[]

beforeEach(() => {
  calls = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      calls.push({ input, init })
      return new Response(null, { status: 200 })
    }),
  )
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('httpClient — json payload', () => {
  it('JSON.stringifies `json` and adds Content-Type: application/json by default', async () => {
    await httpRequest({ path: '/x', method: 'POST', json: { hello: 'world' } })
    const init = calls[0]!.init!
    expect(init.body).toBe(JSON.stringify({ hello: 'world' }))
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json')
  })

  it('preserves a caller-supplied Content-Type when `json` is used', async () => {
    await httpRequest({
      path: '/x',
      method: 'POST',
      json: { hello: 'world' },
      headers: { 'Content-Type': 'application/json; charset=utf-8' },
    })
    const init = calls[0]!.init!
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json; charset=utf-8')
  })

  it('does not add a duplicate `Content-Type` when the caller supplied lowercase `content-type`', async () => {
    await httpRequest({
      path: '/x',
      method: 'POST',
      json: { a: 1 },
      headers: { 'content-type': 'application/json; charset=utf-8' },
    })
    const outgoing = calls[0]!.init!.headers as Record<string, string>
    // Case-insensitive check must recognise the caller-supplied variant.
    expect(outgoing['content-type']).toBe('application/json; charset=utf-8')
    // No shadowing / duplicate header with a different casing.
    expect(outgoing['Content-Type']).toBeUndefined()
  })
})

describe('httpClient — raw body pass-through', () => {
  it('passes a FormData body verbatim and does NOT auto-set Content-Type', async () => {
    const form = new FormData()
    form.append('file', new Blob(['abc']), 'a.txt')

    await httpRequest({ path: '/x', method: 'POST', body: form })

    const init = calls[0]!.init!
    // Must be the exact FormData reference — never JSON-serialised.
    expect(init.body).toBe(form)
    // The browser (fetch) supplies multipart/form-data with the correct
    // boundary; httpClient must not pre-empt that.
    expect((init.headers as Record<string, string>)['Content-Type']).toBeUndefined()
  })

  it('passes a string body through without JSON.stringify and without setting Content-Type', async () => {
    await httpRequest({ path: '/x', method: 'POST', body: 'raw-text' })
    const init = calls[0]!.init!
    expect(init.body).toBe('raw-text')
    expect((init.headers as Record<string, string>)['Content-Type']).toBeUndefined()
  })

  it('preserves a caller-supplied Content-Type when `body` is used', async () => {
    await httpRequest({
      path: '/x',
      method: 'POST',
      body: 'a=1&b=2',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    })
    const init = calls[0]!.init!
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/x-www-form-urlencoded')
  })
})

describe('httpClient — misuse and defaults', () => {
  it('throws synchronously when both `json` and `body` are supplied', () => {
    expect(() =>
      httpRequest({ path: '/x', method: 'POST', json: { a: 1 }, body: 'raw' }),
    ).toThrow(/mutually exclusive|not both/i)
  })

  it('defaults method to GET when omitted', async () => {
    await httpRequest({ path: '/x' })
    expect(calls[0]!.init?.method).toBe('GET')
  })

  it('forwards credentials and signal verbatim', async () => {
    const controller = new AbortController()
    await httpRequest({
      path: '/x',
      method: 'POST',
      json: { a: 1 },
      credentials: 'include',
      signal: controller.signal,
    })
    const init = calls[0]!.init!
    expect(init.credentials).toBe('include')
    expect(init.signal).toBe(controller.signal)
  })
})
