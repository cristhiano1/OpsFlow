// Single-flight refresh coordinator. At most ONE POST /api/v1/auth/refresh
// may be in flight at a time. Concurrent callers all await the same promise,
// so N parallel 401s produce exactly ONE network refresh.
//
// Epoch guard: the current session generation is captured before the refresh
// starts. If `invalidateSession` is called while the refresh is in flight
// (e.g. the user logs out, or a terminal auth failure occurs elsewhere), the
// generation no longer matches when the refresh returns, and the token is
// discarded rather than restored. `setAccessToken` enforces this atomically.
//
// Note on outcomes: `unavailable` covers any non-authoritative failure
// (network error, unexpected server status). Callers MUST NOT invalidate the
// session on an `unavailable` outcome — connectivity issues are recoverable
// and must not log the user out. Only `unauthenticated` (a real 401 from the
// backend) reflects a session that is genuinely gone.

import { refresh as authRefresh } from './authApi'
import type { RefreshResponse } from './contracts'
import { currentGeneration, setAccessToken } from './sessionStore'

export type RefreshResult =
  | { kind: 'refreshed'; data: RefreshResponse }
  | { kind: 'unauthenticated' }
  // Refresh succeeded at the transport level but the session was invalidated
  // while the request was in flight; the returned token was discarded.
  | { kind: 'stale' }
  // Transport-level or unexpected-server failure. Session state is unchanged.
  | { kind: 'unavailable'; error: unknown }

let pending: Promise<RefreshResult> | null = null

export function refreshOnce(): Promise<RefreshResult> {
  if (pending !== null) {
    return pending
  }

  const capturedGeneration = currentGeneration()
  pending = (async (): Promise<RefreshResult> => {
    try {
      const outcome = await authRefresh()

      if (currentGeneration() !== capturedGeneration) {
        // Session was invalidated during the refresh. Whatever came back,
        // it does not belong to the current session.
        return { kind: 'stale' }
      }

      if (outcome.kind === 'success') {
        const applied = setAccessToken(outcome.data.accessToken, capturedGeneration)
        if (!applied) {
          // Extremely narrow race window: generation matched pre-check but was
          // bumped between the check and the write. Treated identically.
          return { kind: 'stale' }
        }
        return { kind: 'refreshed', data: outcome.data }
      }

      if (outcome.kind === 'unauthenticated') {
        return { kind: 'unauthenticated' }
      }

      return { kind: 'unavailable', error: outcome.error }
    } finally {
      // Cleared unconditionally so the NEXT expiration cycle starts a fresh
      // refresh instead of returning a settled promise.
      pending = null
    }
  })()

  return pending
}

// Test-only helper: forcibly clears the pending slot. Real callers never need
// this; the module clears it in `finally` after every refresh cycle.
export function _resetSingleFlightForTests(): void {
  pending = null
}
