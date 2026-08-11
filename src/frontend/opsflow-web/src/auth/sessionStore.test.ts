import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  _resetSessionStoreForTests,
  clearAccessToken,
  currentGeneration,
  getAccessToken,
  getBoundEpoch,
  getPrincipal,
  getSession,
  invalidateSession,
  type Principal,
  samePrincipal,
  setSession,
  subscribeSessionInvalidation,
} from './sessionStore'

const PRINCIPAL_A: Principal = {
  userId: '11111111-1111-1111-1111-111111111111',
  organizationId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
}
const PRINCIPAL_B: Principal = {
  userId: '22222222-2222-2222-2222-222222222222',
  organizationId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
}

describe('sessionStore — token + principal', () => {
  beforeEach(() => {
    _resetSessionStoreForTests()
  })

  it('returns null token, principal and bound epoch before any session is set', () => {
    expect(getAccessToken()).toBeNull()
    expect(getPrincipal()).toBeNull()
    expect(getBoundEpoch()).toBeNull()
    expect(getSession()).toEqual({ token: null, principal: null, boundEpoch: null })
  })

  // Test A: local session binds token + principal + bound epoch atomically.
  it('atomically installs token AND principal AND bound epoch when the generation matches', () => {
    const applied = setSession('token-1', PRINCIPAL_A, 'epoch-1', currentGeneration())
    expect(applied).toBe(true)
    expect(getAccessToken()).toBe('token-1')
    expect(getPrincipal()).toEqual(PRINCIPAL_A)
    expect(getBoundEpoch()).toBe('epoch-1')
    // getSession() returns all three from one consistent point in time.
    expect(getSession()).toEqual({
      token: 'token-1',
      principal: PRINCIPAL_A,
      boundEpoch: 'epoch-1',
    })
  })

  it('clears both token and principal without bumping the generation', () => {
    setSession('token-1', PRINCIPAL_A, 'epoch-1', currentGeneration())
    const before = currentGeneration()

    clearAccessToken()

    expect(getAccessToken()).toBeNull()
    expect(getPrincipal()).toBeNull()
    expect(currentGeneration()).toBe(before)
  })

  it('invalidateSession clears token AND principal AND bumps the generation', () => {
    setSession('token-1', PRINCIPAL_A, 'epoch-1', currentGeneration())
    const before = currentGeneration()

    const after = invalidateSession()

    expect(getAccessToken()).toBeNull()
    expect(getPrincipal()).toBeNull()
    expect(after).toBe(before + 1)
    expect(currentGeneration()).toBe(before + 1)
  })

  it('rejects setSession carrying a stale generation (token AND principal discarded)', () => {
    const capturedBefore = currentGeneration()
    invalidateSession()

    const applied = setSession('token-from-stale-caller', PRINCIPAL_B, 'epoch-x', capturedBefore)

    expect(applied).toBe(false)
    expect(getAccessToken()).toBeNull()
    expect(getPrincipal()).toBeNull()
  })

  it('accepts setSession again after invalidateSession + fresh capture', () => {
    invalidateSession()
    const fresh = currentGeneration()

    const applied = setSession('token-post-invalidation', PRINCIPAL_B, 'epoch-2', fresh)

    expect(applied).toBe(true)
    expect(getAccessToken()).toBe('token-post-invalidation')
    expect(getPrincipal()).toEqual(PRINCIPAL_B)
  })

  it('never leaves a torn state (token from B while principal still says A) on a rejected write', () => {
    // Install A.
    setSession('token-A', PRINCIPAL_A, 'epoch-A', currentGeneration())
    // A stale caller (captured the pre-invalidation generation) tries to
    // install B's token after a local invalidation.
    const staleGen = currentGeneration()
    invalidateSession()
    const applied = setSession('token-B', PRINCIPAL_B, 'epoch-B', staleGen)

    expect(applied).toBe(false)
    // Neither B's token, principal, NOR bound epoch leaked in; and A was
    // cleared by invalidateSession — the store is fully null, never torn.
    expect(getSession()).toEqual({ token: null, principal: null, boundEpoch: null })
  })
})

describe('samePrincipal', () => {
  it('is true for identical userId + organizationId', () => {
    expect(samePrincipal(PRINCIPAL_A, { ...PRINCIPAL_A })).toBe(true)
  })

  it('is false when userId differs', () => {
    expect(samePrincipal(PRINCIPAL_A, { ...PRINCIPAL_A, userId: 'x' })).toBe(false)
  })

  it('is false when organizationId differs', () => {
    expect(samePrincipal(PRINCIPAL_A, { ...PRINCIPAL_A, organizationId: 'x' })).toBe(false)
  })

  it('treats null vs non-null as different, and null vs null as equal', () => {
    expect(samePrincipal(null, PRINCIPAL_A)).toBe(false)
    expect(samePrincipal(PRINCIPAL_A, null)).toBe(false)
    expect(samePrincipal(null, null)).toBe(true)
  })
})

describe('sessionStore does not touch Web Storage', () => {
  const getItemSpy = vi.spyOn(Storage.prototype, 'getItem')
  const setItemSpy = vi.spyOn(Storage.prototype, 'setItem')
  const removeItemSpy = vi.spyOn(Storage.prototype, 'removeItem')

  beforeEach(() => {
    getItemSpy.mockClear()
    setItemSpy.mockClear()
    removeItemSpy.mockClear()
    _resetSessionStoreForTests()
  })

  afterEach(() => {
    getItemSpy.mockClear()
    setItemSpy.mockClear()
    removeItemSpy.mockClear()
  })

  it('never calls localStorage or sessionStorage through any operation', () => {
    setSession('token-1', PRINCIPAL_A, 'epoch-1', currentGeneration())
    getAccessToken()
    getPrincipal()
    clearAccessToken()
    invalidateSession()
    setSession('token-2', PRINCIPAL_B, 'epoch-2', currentGeneration())
    getAccessToken()

    expect(getItemSpy).not.toHaveBeenCalled()
    expect(setItemSpy).not.toHaveBeenCalled()
    expect(removeItemSpy).not.toHaveBeenCalled()
  })
})

describe('subscribeSessionInvalidation', () => {
  beforeEach(() => {
    _resetSessionStoreForTests()
  })

  it('notifies listeners synchronously on invalidateSession', () => {
    const listener = vi.fn()
    subscribeSessionInvalidation(listener)

    setSession('token-1', PRINCIPAL_A, 'epoch-1', currentGeneration())
    expect(listener).not.toHaveBeenCalled()

    invalidateSession()

    expect(listener).toHaveBeenCalledTimes(1)
  })

  it('stops notifying after unsubscribe', () => {
    const listener = vi.fn()
    const unsubscribe = subscribeSessionInvalidation(listener)

    invalidateSession()
    expect(listener).toHaveBeenCalledTimes(1)

    unsubscribe()
    invalidateSession()
    expect(listener).toHaveBeenCalledTimes(1)
  })

  it('clears all listeners on reset', () => {
    const listener = vi.fn()
    subscribeSessionInvalidation(listener)

    _resetSessionStoreForTests()
    invalidateSession()

    expect(listener).not.toHaveBeenCalled()
  })

  it('isolates faulty subscribers: later listeners still fire and invalidateSession returns', () => {
    const listenerA = vi.fn(() => { throw new Error('boom') })
    const listenerB = vi.fn()
    subscribeSessionInvalidation(listenerA)
    subscribeSessionInvalidation(listenerB)

    setSession('token-1', PRINCIPAL_A, 'epoch-1', currentGeneration())
    const before = currentGeneration()

    const after = invalidateSession()

    expect(listenerA).toHaveBeenCalledTimes(1)
    expect(listenerB).toHaveBeenCalledTimes(1)
    expect(after).toBe(before + 1)
    expect(currentGeneration()).toBe(before + 1)
    expect(getAccessToken()).toBeNull()
    expect(getPrincipal()).toBeNull()
    expect(getBoundEpoch()).toBeNull()
  })

  it('allows idempotent unsubscribe without error', () => {
    const listener = vi.fn()
    const unsubscribe = subscribeSessionInvalidation(listener)

    unsubscribe()
    unsubscribe()

    invalidateSession()
    expect(listener).not.toHaveBeenCalled()
  })
})
