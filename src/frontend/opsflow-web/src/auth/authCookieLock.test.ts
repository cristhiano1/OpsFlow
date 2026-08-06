import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  AUTH_COOKIE_LOCK_NAME,
  AuthLockUnavailableError,
  withAuthCookieLock,
} from './authCookieLock'

// ────────────────────────────────────────────────────────────────────────
// Minimal fake Web Locks — same shape as singleFlightRefresh.test.ts /
// authApi.test.ts, kept local so this file is self-contained.
// ────────────────────────────────────────────────────────────────────────

interface FakeLockHarness {
  manager: unknown
  requestCalls: Array<{ name: string }>
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

afterEach(() => {
  vi.unstubAllGlobals()
  uninstallLocks()
})

describe('authCookieLock — shared constant', () => {
  it('exports the fixed same-origin lock name "opsflow.auth.refresh"', () => {
    // The literal is asserted here (the one authoritative place); every other
    // test must reference the exported constant so login / refresh / logout
    // are provably serialised through the SAME mutex.
    expect(AUTH_COOKIE_LOCK_NAME).toBe('opsflow.auth.refresh')
  })
})

describe('authCookieLock — happy path', () => {
  beforeEach(() => {
    installFakeLocks(makeFakeLockManager().manager)
  })

  it('acquires navigator.locks with the exported constant and runs the operation inside the callback', async () => {
    // Fresh harness so we can inspect requestCalls.
    uninstallLocks()
    const lock = makeFakeLockManager()
    installFakeLocks(lock.manager)

    let operationRan = false
    const result = await withAuthCookieLock(async () => {
      operationRan = true
      return 'done'
    })

    expect(result).toBe('done')
    expect(operationRan).toBe(true)
    expect(lock.requestCalls).toEqual([{ name: AUTH_COOKIE_LOCK_NAME }])
  })
})

describe('authCookieLock — fail-closed availability', () => {
  it('throws AuthLockUnavailableError WITHOUT invoking the operation when navigator.locks is missing', async () => {
    uninstallLocks()

    let operationRan = false
    const op = async (): Promise<number> => {
      operationRan = true
      return 1
    }

    await expect(withAuthCookieLock(op)).rejects.toBeInstanceOf(AuthLockUnavailableError)
    expect(operationRan).toBe(false)
  })

  it('propagates a throw from navigator.locks.request without wrapping', async () => {
    const throwing = makeFakeLockManager()
    throwing.throwOnAcquire = new Error('lock kernel died')
    installFakeLocks(throwing.manager)

    let operationRan = false
    await expect(
      withAuthCookieLock(async () => {
        operationRan = true
        return 1
      }),
    ).rejects.toThrow('lock kernel died')
    expect(operationRan).toBe(false)
  })
})

describe('authCookieLock — error propagation from the operation', () => {
  beforeEach(() => {
    installFakeLocks(makeFakeLockManager().manager)
  })

  it('propagates a throw from the operation callback to the caller', async () => {
    const err = new Error('operation failed')
    await expect(
      withAuthCookieLock(async () => {
        throw err
      }),
    ).rejects.toBe(err)
  })
})
