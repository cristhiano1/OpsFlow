// Shared cross-tab mutex for every frontend operation that writes, rotates,
// or deletes the browser-wide HttpOnly refresh cookie. Login, refresh, and
// logout ALL serialize through this ONE lock so their Set-Cookie responses
// cannot arrive out of order and overwrite/delete each other.
//
// The lock name is a single exported constant — never a copied string
// literal — so callers and tests can prove they use the same mutex.
//
// Availability policy: if `navigator.locks` is unavailable, `withAuthCookieLock`
// throws `AuthLockUnavailableError` WITHOUT invoking the operation. Callers
// map this to their own "unavailable" outcome (`{ kind: 'unavailable', error }`)
// so that a missing / broken Web Locks environment fails closed instead of
// silently letting the cookie mutation happen uncoordinated. Session state
// is not touched.

// Fixed same-origin lock name. Every tab of this origin acquires the SAME
// name, giving mutual exclusion across contexts.
export const AUTH_COOKIE_LOCK_NAME = 'opsflow.auth.refresh'

export class AuthLockUnavailableError extends Error {
  readonly cause: unknown

  constructor(cause: unknown) {
    super(
      'navigator.locks is not available; auth cookie mutation cannot be safely serialised across tabs.',
    )
    this.name = 'AuthLockUnavailableError'
    this.cause = cause
  }
}

// Runs `operation` while holding the shared auth-cookie lock. The lock is
// held for the ENTIRE duration of `operation` (including any awaited work
// inside it), so the HTTP request AND the browser's processing of the
// Set-Cookie response both happen while the mutex is held.
//
// This helper deliberately does NOT swallow errors. Error surfaces:
//   - missing / unavailable `navigator.locks` → throws
//     `AuthLockUnavailableError` (the ONLY error this helper synthesises).
//   - `navigator.locks.request(...)` rejection/throw → propagated to the
//     caller unchanged (not wrapped).
//   - `operation()` rejection/throw → propagated to the caller unchanged
//     (after the lock is released).
//
// It is safe to nest a single Web Lock acquisition per call — this helper
// is the SINGLE entrypoint for the auth-cookie lock, so callers must not
// also invoke `navigator.locks.request` for the same name.
export async function withAuthCookieLock<T>(operation: () => Promise<T>): Promise<T> {
  // Fail-closed availability check. lib.dom.d.ts types `navigator.locks` as
  // always-present, but in reality it is absent in insecure contexts,
  // non-Window realms, and older browsers.
  const locks = typeof navigator === 'undefined'
    ? undefined
    : (navigator as Navigator & { locks?: LockManager }).locks
  if (locks === undefined || typeof locks.request !== 'function') {
    throw new AuthLockUnavailableError(
      new Error('navigator.locks is not available'),
    )
  }

  return await locks.request(AUTH_COOKIE_LOCK_NAME, async () => await operation())
}
