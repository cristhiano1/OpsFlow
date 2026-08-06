// Single-flight refresh coordinator. At most ONE POST /api/v1/auth/refresh
// may be in flight PER TAB (module-scoped `pending`) AND at most one refresh
// may execute at a time PER ORIGIN (via the shared `withAuthCookieLock`
// mutex). Concurrent callers in the same tab share the same in-flight
// promise, and concurrent tabs on the same origin serialize behind the
// browser lock — so N tabs × M concurrent callers still produces at most
// one POST /refresh per lock acquisition. The shared HttpOnly refresh
// cookie therefore rotates cleanly instead of racing itself, which would
// otherwise trigger the backend's refresh-token reuse-detection revocation.
//
// The same lock ALSO serialises login and logout (which write and delete
// the same cookie). See `authCookieLock.ts` for the single shared name.
//
// Epoch guard: the session generation is TAB-LOCAL memory. It is captured
// before the refresh is queued behind the lock and re-checked AFTER
// acquiring the lock and AGAIN after the network refresh returns. These
// checks protect only against a LOCAL logout / session invalidation in
// THIS tab — a sibling tab refreshing the shared HttpOnly cookie does NOT
// modify this tab's generation. What the cross-tab lock guarantees is that
// once we run, we run sequentially with (not concurrently with) any sibling
// refresh, so we see whatever rotated cookie the browser has stored by then.
// Any generation mismatch discards the incoming token instead of restoring
// it.
//
// Availability policy: if `withAuthCookieLock` reports the Web Locks API
// as unavailable (or the lock request itself throws), we return
// `unavailable` WITHOUT issuing POST /refresh. This is intentionally
// fail-closed — silently falling back to an uncoordinated refresh across
// tabs would race the shared HttpOnly cookie. Session state is preserved
// so a later call can retry when the runtime recovers.

import { refresh as authRefresh } from './authApi'
import { withAuthCookieLock } from './authCookieLock'
import type { RefreshResponse } from './contracts'
import { currentGeneration, setAccessToken } from './sessionStore'

export type RefreshResult =
  | { kind: 'refreshed'; data: RefreshResponse }
  | { kind: 'unauthenticated' }
  // Refresh was superseded — the LOCAL session generation moved between
  // capture and completion (i.e. a logout or terminal auth failure in THIS
  // tab bumped the generation). The response, if any, was discarded.
  | { kind: 'stale' }
  // Transport / unexpected-server failure OR cross-tab lock unavailable.
  // Session state is unchanged; a later call may retry.
  | { kind: 'unavailable'; error: unknown }

let pending: Promise<RefreshResult> | null = null

async function runRefreshCycle(capturedGeneration: number): Promise<RefreshResult> {
  try {
    return await withAuthCookieLock(async (): Promise<RefreshResult> => {
      // Re-check generation NOW that we hold the cross-tab lock. Only a
      // LOCAL logout could have bumped the generation while we waited —
      // sibling-tab refreshes are serialised behind the same lock and do
      // NOT touch this tab's generation. This check aborts the refresh if
      // THIS tab logged out during the wait.
      if (currentGeneration() !== capturedGeneration) {
        return { kind: 'stale' }
      }

      const outcome = await authRefresh()

      // Post-refresh generation check. Same rationale as above: only a
      // LOCAL logout during the network round-trip could have bumped the
      // generation.
      if (currentGeneration() !== capturedGeneration) {
        return { kind: 'stale' }
      }

      if (outcome.kind === 'success') {
        const applied = setAccessToken(outcome.data.accessToken, capturedGeneration)
        if (!applied) {
          // Extremely narrow race window: generation matched the pre-check
          // but was bumped between the check and the write. Treated
          // identically to the earlier `stale` branch.
          return { kind: 'stale' }
        }
        return { kind: 'refreshed', data: outcome.data }
      }

      if (outcome.kind === 'unauthenticated') {
        return { kind: 'unauthenticated' }
      }

      return { kind: 'unavailable', error: outcome.error }
    })
  } catch (error) {
    // Lock acquisition itself failed (or the operation threw). Session
    // state untouched.
    return { kind: 'unavailable', error }
  }
}

export function refreshOnce(): Promise<RefreshResult> {
  if (pending !== null) {
    return pending
  }

  const capturedGeneration = currentGeneration()
  // `.finally()` clears the pending slot as a MICROTASK after the outer
  // assignment below has completed. An inline `try/finally { pending = null }`
  // inside the IIFE would race the outer assignment when the async body
  // resolves synchronously (e.g. the unavailable path returns without
  // awaiting), leaving pending set to the already-settled promise and
  // preventing the next cycle from starting.
  pending = runRefreshCycle(capturedGeneration).finally(() => {
    pending = null
  })

  return pending
}

// Test-only helper: forcibly clears the pending slot. Real callers never need
// this; the module clears it in `finally` after every refresh cycle.
export function _resetSingleFlightForTests(): void {
  pending = null
}
