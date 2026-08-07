// Single-flight refresh coordinator. At most ONE POST /api/v1/auth/refresh
// may be in flight PER TAB (module-scoped `pending`) AND at most one refresh
// may execute at a time PER ORIGIN (via the shared `withAuthCookieLock`
// mutex, which ALSO serialises login and logout). N tabs × M concurrent
// callers still produce at most one POST /refresh per lock acquisition.
//
// This module enforces THREE independent guards, plus the lock:
//
//   • LOCAL generation (sessionStore): aborts if THIS tab logged out while
//     the refresh was queued or in flight.
//
//   • SHARED session epoch (sessionEpoch): aborts if ANOTHER tab replaced
//     the browser-wide session (login/logout elsewhere) while this refresh
//     was queued or in flight. The epoch is re-read AFTER acquiring the lock
//     — critical, because a sibling tab may be ahead of us in the lock queue
//     and perform a login while we wait, rotating the shared cookie. If we
//     refreshed anyway we would install the SIBLING's token into THIS tab's
//     original request flow. That is the P1 this module prevents.
//
//   • PRINCIPAL identity (defense in depth): the account the refreshed token
//     belongs to must match the account that originated the request. The
//     check happens BEFORE `setSession`, so a foreign token/principal is
//     NEVER installed as a continuation of the old session.
//
// A normal successful refresh rotates CREDENTIALS for the SAME logical
// session and therefore does NOT rotate the shared epoch (login/logout do).
// A cold-bootstrap refresh (no epoch established yet) MAY establish the
// initial epoch — that is establishment, not a session switch — and, having
// no prior principal, installs whatever principal the refresh returns.
//
// Availability policy: if the Web Locks API, the shared epoch storage, or
// secure randomness is unavailable, we return `unavailable` WITHOUT issuing
// (or without acting on) POST /refresh. This is intentionally fail-closed —
// an uncoordinated refresh could race the shared HttpOnly cookie or miss a
// cross-tab session replacement. Session state is preserved so a later call
// can retry when the runtime recovers.

import { refresh as authRefresh } from './authApi'
import { withAuthCookieLock } from './authCookieLock'
import type { RefreshResponse } from './contracts'
import {
  type ExpectedEpoch,
  readEpoch,
  rotateEpoch,
  sameExpectedEpoch,
  SessionEpochUnavailableError,
} from './sessionEpoch'
import {
  currentGeneration,
  type Principal,
  samePrincipal,
  setSession,
} from './sessionStore'

export type RefreshResult =
  | { kind: 'refreshed'; data: RefreshResponse; sessionEpoch: string }
  | { kind: 'unauthenticated' }
  // Superseded by a LOCAL change — this tab's generation moved (a local
  // logout / terminal auth failure) between capture and completion.
  | { kind: 'stale' }
  // Superseded by a CROSS-TAB change — either the shared session epoch no
  // longer matches the epoch captured by the originating request, or the
  // refreshed principal differs from the originating principal (a sibling
  // tab logged in/out). No token was installed; the caller must NOT retry.
  | { kind: 'session-replaced' }
  // Transport / unexpected-server failure, cross-tab lock unavailable, OR
  // shared-epoch storage / secure-randomness unavailable. State unchanged.
  | { kind: 'unavailable'; error: unknown }

// The single-flight slot is keyed by the LOGICAL SESSION identity of the
// refresh (expected epoch + expected principal). Callers with the same
// identity share one in-flight promise / one POST. A caller with a DIFFERENT
// identity must NOT consume the in-flight result — it waits for the current
// operation to settle and then evaluates its own refresh, so two same-tab
// refreshes never run concurrently.
interface PendingRefresh {
  expected: ExpectedEpoch
  principal: Principal | null
  promise: Promise<RefreshResult>
}

let pending: PendingRefresh | null = null

function sameLogicalSession(
  a: PendingRefresh,
  expected: ExpectedEpoch,
  principal: Principal | null,
): boolean {
  return sameExpectedEpoch(a.expected, expected) && samePrincipal(a.principal, principal)
}

async function runRefreshCycle(
  capturedGeneration: number,
  expected: ExpectedEpoch,
  expectedPrincipal: Principal | null,
): Promise<RefreshResult> {
  try {
    return await withAuthCookieLock(async (): Promise<RefreshResult> => {
      // Local generation guard: only a LOCAL logout could have bumped this
      // while we waited for the lock. Sibling-tab refreshes do not touch it.
      if (currentGeneration() !== capturedGeneration) {
        return { kind: 'stale' }
      }

      // Cross-tab epoch guard, re-read AFTER acquiring the lock. A sibling
      // tab ahead of us in the lock queue may have logged in/out (or created
      // the initial epoch) while we waited. `effectiveEpoch` is the epoch the
      // newly refreshed token will be BOUND to.
      const read = readEpoch()
      if (read.status === 'unavailable') {
        // Cannot evaluate the cross-tab invariant — fail closed.
        return { kind: 'unavailable', error: read.error }
      }

      let effectiveEpoch: string
      if (expected.kind === 'present') {
        // Only the EXACT captured epoch may refresh. `missing` (marker
        // cleared) or a different value both mean the session we captured is
        // gone.
        if (read.status !== 'present' || read.epoch !== expected.epoch) {
          return { kind: 'session-replaced' }
        }
        effectiveEpoch = expected.epoch
      } else {
        // expected.kind === 'missing'. THE P1 GUARD: a request that captured
        // "no epoch" must NOT adopt an epoch that appeared afterwards (a
        // sibling login/logout). If an epoch is now PRESENT, the session was
        // replaced since capture — reject with ZERO POST.
        if (read.status === 'present') {
          return { kind: 'session-replaced' }
        }
        // Still missing under the lock (login/logout would need this same
        // lock to create one), so THIS caller legitimately establishes the
        // initial epoch and binds the cold refresh to it.
        effectiveEpoch = rotateEpoch()
      }

      const outcome = await authRefresh()

      // Post-response local generation guard.
      if (currentGeneration() !== capturedGeneration) {
        return { kind: 'stale' }
      }

      // Post-response cross-tab epoch guard (defense in depth): the shared
      // epoch must still be exactly the one this token is being bound to.
      // login/logout cannot acquire the lock during our HTTP round-trip, but
      // re-check anyway before installing any token.
      const postRead = readEpoch()
      if (postRead.status === 'unavailable') {
        return { kind: 'unavailable', error: postRead.error }
      }
      if (postRead.status !== 'present' || postRead.epoch !== effectiveEpoch) {
        return { kind: 'session-replaced' }
      }

      if (outcome.kind === 'success') {
        const refreshedPrincipal: Principal = {
          userId: outcome.data.user.userId,
          organizationId: outcome.data.user.organizationId,
        }
        // PRINCIPAL guard BEFORE install: a refresh that belongs to a
        // different account must NEVER be installed as a continuation of the
        // originating session. For a cold bootstrap (`expectedPrincipal ===
        // null`) there is no prior account to contradict, so we install
        // whatever principal the refresh returns.
        if (expectedPrincipal !== null && !samePrincipal(refreshedPrincipal, expectedPrincipal)) {
          return { kind: 'session-replaced' }
        }

        // Atomic install: token + principal + bound epoch together. For a
        // normal refresh `effectiveEpoch` is the unchanged expected epoch;
        // for a cold bootstrap it is the epoch just established above.
        const applied = setSession(
          outcome.data.accessToken,
          refreshedPrincipal,
          effectiveEpoch,
          capturedGeneration,
        )
        if (!applied) {
          // Narrow race: generation matched the pre-check but was bumped
          // between the check and the atomic write. Same as `stale`.
          return { kind: 'stale' }
        }
        return { kind: 'refreshed', data: outcome.data, sessionEpoch: effectiveEpoch }
      }

      if (outcome.kind === 'unauthenticated') {
        return { kind: 'unauthenticated' }
      }

      return { kind: 'unavailable', error: outcome.error }
    })
  } catch (error) {
    if (error instanceof SessionEpochUnavailableError) {
      // Fail closed: storage / randomness for the cross-tab guard is unusable.
      return { kind: 'unavailable', error }
    }
    // Lock acquisition itself failed (or the operation threw). Session
    // state untouched.
    return { kind: 'unavailable', error }
  }
}

// Coordinated refresh. Callers pass the identity of the logical session that
// originated the work, captured BEFORE the originating request went out:
//   - `expected`: the shared-epoch EXPECTATION at capture — `{ kind: 'present',
//     epoch }` when an epoch existed, or `{ kind: 'missing' }` when none did.
//     A `missing` expectation may only establish the initial epoch if the
//     epoch is STILL missing under the lock; if one has appeared it is
//     rejected as session-replaced (the P1 fix).
//   - `expectedPrincipal`: the account the originating token belonged to, or
//     `null` for a cold caller with no prior principal.
//
// Concurrent callers with the SAME logical session identity share one
// in-flight promise (and thus one lock acquisition / one POST).
//
// INVARIANT (multi-waiter safety): this function performs NO `await` before it
// assigns `pending` below, so installing a pending entry is SYNCHRONOUS and
// atomic with respect to other synchronous `refreshOnce` calls. Consequences:
//   • the `sameLogicalSession` check below IS the re-evaluation — a later
//     caller with the SAME identity always observes the just-installed entry
//     and shares its promise (never starts a second cycle for that identity);
//   • a later caller with a DIFFERENT identity chains off the LATEST pending
//     (whichever was installed most recently), producing a strictly serial
//     chain A → B → C …;
//   • two `runRefreshCycle`s never overlap, because each awaits the previous
//     wrapped promise before invoking its own cycle.
// So N mismatched waiters behind an old pending resolve to at most ONE
// same-tab refresh POST in flight at a time.
export function refreshOnce(
  expected: ExpectedEpoch,
  expectedPrincipal: Principal | null,
): Promise<RefreshResult> {
  // Same logical session as the in-flight refresh → share it (one POST).
  if (pending !== null && sameLogicalSession(pending, expected, expectedPrincipal)) {
    return pending.promise
  }

  // A different logical session while one is pending: DO NOT consume its
  // result. Wait for it to settle (ignoring its outcome), then evaluate this
  // session's own refresh. This serialises same-tab refreshes.
  const previous = pending?.promise ?? null

  const run = (async (): Promise<RefreshResult> => {
    if (previous !== null) {
      await previous.catch(() => {})
    }
    // Capture the generation at the moment THIS cycle actually begins, so a
    // local logout during the wait is observed.
    return runRefreshCycle(currentGeneration(), expected, expectedPrincipal)
  })()

  // Wrap with a `.finally` that clears the slot only if it is still THIS
  // entry — avoids a cleanup race clobbering a newer pending entry.
  const wrapped = run.finally(() => {
    if (pending !== null && pending.promise === wrapped) {
      pending = null
    }
  })
  pending = { expected, principal: expectedPrincipal, promise: wrapped }
  return wrapped
}

// Test-only helper: forcibly clears the pending slot.
export function _resetSingleFlightForTests(): void {
  pending = null
}
