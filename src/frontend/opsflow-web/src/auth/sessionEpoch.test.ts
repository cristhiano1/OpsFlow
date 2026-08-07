import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  _resetSessionEpochForTests,
  ensureEpoch,
  readEpoch,
  rotateEpoch,
  SESSION_EPOCH_STORAGE_KEY,
  SessionEpochUnavailableError,
} from './sessionEpoch'

beforeEach(() => {
  localStorage.clear()
  _resetSessionEpochForTests()
})

afterEach(() => {
  vi.restoreAllMocks()
  localStorage.clear()
})

describe('sessionEpoch — storage key and stored value', () => {
  it('uses the fixed opaque key and stores ONLY an opaque marker (no secrets)', () => {
    expect(SESSION_EPOCH_STORAGE_KEY).toBe('opsflow.auth.session-epoch')

    const value = ensureEpoch()
    expect(localStorage.getItem(SESSION_EPOCH_STORAGE_KEY)).toBe(value)

    expect(value).not.toMatch(/token|bearer|@|user|org|role|email/i)
    const authKeys = Object.keys(localStorage).filter((k) => k.startsWith('opsflow.auth'))
    expect(authKeys).toEqual([SESSION_EPOCH_STORAGE_KEY])
  })
})

describe('sessionEpoch — readEpoch distinguishes present / missing / unavailable', () => {
  it('returns { status: "missing" } when localStorage works but the key is absent', () => {
    expect(readEpoch()).toEqual({ status: 'missing' })
  })

  it('returns { status: "present", epoch } when the key exists', () => {
    localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, 'epoch-x')
    expect(readEpoch()).toEqual({ status: 'present', epoch: 'epoch-x' })
  })

  it('returns { status: "unavailable" } (NOT missing) when getItem throws', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('denied')
    })
    const read = readEpoch()
    expect(read.status).toBe('unavailable')
    // Critically: unavailable is a DISTINCT state from missing, so two
    // unreadable reads never accidentally compare equal via a shared null.
    expect(read).not.toEqual({ status: 'missing' })
  })
})

describe('sessionEpoch — ensure / rotate', () => {
  it('ensureEpoch creates a marker when absent and is idempotent when present', () => {
    const first = ensureEpoch()
    expect(readEpoch()).toEqual({ status: 'present', epoch: first })
    expect(ensureEpoch()).toBe(first)
  })

  it('rotateEpoch always writes a NEW distinct value', () => {
    const a = ensureEpoch()
    const b = rotateEpoch()
    const c = rotateEpoch()
    expect(b).not.toBe(a)
    expect(c).not.toBe(b)
    expect(readEpoch()).toEqual({ status: 'present', epoch: c })
  })
})

describe('sessionEpoch — fail-closed availability', () => {
  it('touches ONLY the single marker key (no probe / no second auth-prefixed key)', () => {
    ensureEpoch()
    rotateEpoch()
    readEpoch()
    // The write-availability check is the real epoch write itself, so no
    // extra key is ever created.
    const authKeys = Object.keys(localStorage).filter((k) => k.startsWith('opsflow.auth'))
    expect(authKeys).toEqual([SESSION_EPOCH_STORAGE_KEY])
  })

  it('ensureEpoch / rotateEpoch throw SessionEpochUnavailableError when setItem throws (write fails closed)', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('QuotaExceededError')
    })
    expect(() => ensureEpoch()).toThrow(SessionEpochUnavailableError)
    expect(() => rotateEpoch()).toThrow(SessionEpochUnavailableError)
  })

  it('ensureEpoch throws SessionEpochUnavailableError when the epoch cannot be READ (getItem throws)', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('denied')
    })
    expect(() => ensureEpoch()).toThrow(SessionEpochUnavailableError)
  })
})

describe('sessionEpoch — secure randomness (no Date.now()/Math.random fallback)', () => {
  it('uses crypto.randomUUID when available', () => {
    const spy = vi.spyOn(crypto, 'randomUUID')
    rotateEpoch()
    expect(spy).toHaveBeenCalled()
  })

  it('falls back to crypto.getRandomValues (16 bytes → 32 hex chars) when randomUUID is absent', () => {
    // Shadow randomUUID with undefined (it lives on Crypto.prototype, so a
    // `delete` on the instance would not hide it) but keep getRandomValues.
    const originalUUID = crypto.randomUUID
    Object.defineProperty(crypto, 'randomUUID', { configurable: true, value: undefined })
    try {
      const value = rotateEpoch()
      expect(value).toMatch(/^[0-9a-f]{32}$/)
    } finally {
      Object.defineProperty(crypto, 'randomUUID', { configurable: true, value: originalUUID })
    }
  })

  it('fails closed with SessionEpochUnavailableError when NO secure RNG is available', () => {
    const originalUUID = crypto.randomUUID
    const originalGRV = crypto.getRandomValues
    Object.defineProperty(crypto, 'randomUUID', { configurable: true, value: undefined })
    Object.defineProperty(crypto, 'getRandomValues', { configurable: true, value: undefined })
    try {
      expect(() => rotateEpoch()).toThrow(SessionEpochUnavailableError)
    } finally {
      Object.defineProperty(crypto, 'randomUUID', { configurable: true, value: originalUUID })
      Object.defineProperty(crypto, 'getRandomValues', { configurable: true, value: originalGRV })
    }
  })
})
