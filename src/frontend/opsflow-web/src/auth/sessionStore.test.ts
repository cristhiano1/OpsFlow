import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  _resetSessionStoreForTests,
  clearAccessToken,
  currentGeneration,
  getAccessToken,
  invalidateSession,
  setAccessToken,
} from './sessionStore'

describe('sessionStore', () => {
  beforeEach(() => {
    _resetSessionStoreForTests()
  })

  it('returns null before any token is set', () => {
    expect(getAccessToken()).toBeNull()
  })

  it('round-trips a token when the captured generation matches', () => {
    const applied = setAccessToken('token-1', currentGeneration())
    expect(applied).toBe(true)
    expect(getAccessToken()).toBe('token-1')
  })

  it('clears the token without bumping the generation', () => {
    setAccessToken('token-1', currentGeneration())
    const before = currentGeneration()

    clearAccessToken()

    expect(getAccessToken()).toBeNull()
    expect(currentGeneration()).toBe(before)
  })

  it('invalidateSession clears the token AND bumps the generation', () => {
    setAccessToken('token-1', currentGeneration())
    const before = currentGeneration()

    const after = invalidateSession()

    expect(getAccessToken()).toBeNull()
    expect(after).toBe(before + 1)
    expect(currentGeneration()).toBe(before + 1)
  })

  it('rejects setAccessToken carrying a stale generation', () => {
    const capturedBefore = currentGeneration()
    invalidateSession()

    const applied = setAccessToken('token-from-stale-caller', capturedBefore)

    expect(applied).toBe(false)
    expect(getAccessToken()).toBeNull()
  })

  it('accepts setAccessToken again after invalidateSession + fresh capture', () => {
    invalidateSession()
    const fresh = currentGeneration()

    const applied = setAccessToken('token-post-invalidation', fresh)

    expect(applied).toBe(true)
    expect(getAccessToken()).toBe('token-post-invalidation')
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
    setAccessToken('token-1', currentGeneration())
    getAccessToken()
    clearAccessToken()
    invalidateSession()
    setAccessToken('token-2', currentGeneration())
    getAccessToken()

    expect(getItemSpy).not.toHaveBeenCalled()
    expect(setItemSpy).not.toHaveBeenCalled()
    expect(removeItemSpy).not.toHaveBeenCalled()
  })
})
