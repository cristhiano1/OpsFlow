// Raw authentication API. Every function goes through the low-level
// httpClient and NEVER through apiClient's automatic-refresh layer, so that:
//   - login / refresh / logout cannot recursively trigger themselves;
//   - the bootstrap and single-flight refresh paths always see the API's
//     real 401, not a synthesised retry outcome.
//
// Cookie-mutation coordination: login and logout both write / delete the
// browser-wide HttpOnly refresh cookie, and therefore MUST serialise with
// refresh via the shared `withAuthCookieLock` mutex. `authApi.refresh` is
// the exception — it is the low-level primitive that `singleFlightRefresh`
// invokes INSIDE the already-held lock, so it must NOT acquire the lock
// itself (that would nest the lock and deadlock).
//
// Session-epoch coordination — the PRECOMMIT protocol. A successful login or
// logout REPLACES the browser-wide session, so both advance the shared epoch
// (see sessionEpoch.ts) using an OLD → precommit NEXT → HTTP → keep-or-rollback
// transition, all inside the shared auth-cookie lock:
//
//   1. read OLD (fail closed if it cannot be read)
//   2. write NEXT to shared storage BEFORE the HTTP mutation (precommit)
//   3. perform the cookie-mutating POST
//   4. resolve the outcome by whether the refresh cookie could have changed:
//
// AMBIGUITY RULE. After the POST is dispatched, a thrown error or a
// successful-status-then-parse-failure does NOT prove the browser did not
// already process a Set-Cookie. Restoring OLD when the cookie actually became
// NEW would leave cookie=NEW / epoch=OLD — unsafe. Therefore NEXT is ROLLED
// BACK to OLD **only** for statuses the backend contract proves leave the
// refresh cookie untouched. Anything ambiguous KEEPS NEXT and fails closed.
//
// Backend contract (AuthenticationController):
//   • login writes Set-Cookie ONLY on the 200 path (append precedes Ok); the
//     400 (ValidationProblem) and 401 (UnauthorizedWithoutBody) paths return
//     BEFORE any append → cookie provably untouched → rollback-eligible.
//     A 500 (thrown before append) is not deliberately produced by the
//     controller and is ambiguous from the client → keep NEXT.
//   • logout deletes the cookie ONLY immediately before returning 204; it has
//     NO deliberate non-2xx status → nothing is rollback-eligible after
//     dispatch → logout keeps NEXT for every post-dispatch outcome.
//
// Precommitting NEXT before the cookie changes means a sibling tab that
// acquires the lock next can never observe the NEW cookie paired with the OLD
// epoch. login additionally COMMUNICATES the committed epoch to its caller so
// the caller can bind the local session atomically to token + principal + NEXT.
// On a 200, once Set-Cookie has been processed NEXT is COMMITTED even if the
// later `response.json()` throws — such a failure returns `unavailable` and
// KEEPS NEXT; it never rolls back.
//
// The `unavailable` variant covers ANY non-authoritative failure (network
// error, unexpected/ambiguous server response, thrown fetch, JSON parse
// failure, cross-tab lock / epoch-storage / randomness unavailability, or a
// failed rollback on a rollback-eligible path).

import { withAuthCookieLock } from './authCookieLock'
import { httpRequest } from './httpClient'
import { readEpoch, restoreEpoch, rotateEpoch } from './sessionEpoch'
import type {
  LoginRequest,
  LoginResponse,
  LoginUserResponse,
  RefreshResponse,
} from './contracts'

const AUTH_BASE = '/api/v1/auth'
export const AUTH_LOGIN_PATH = `${AUTH_BASE}/login`
export const AUTH_REFRESH_PATH = `${AUTH_BASE}/refresh`
export const AUTH_LOGOUT_PATH = `${AUTH_BASE}/logout`
export const AUTH_ME_PATH = `${AUTH_BASE}/me`

export type LoginOutcome =
  // `epoch` is the committed NEXT marker; the caller binds the new local
  // session to it atomically (token + principal + epoch).
  | { kind: 'success'; data: LoginResponse; epoch: string }
  | { kind: 'invalid-credentials' }
  | { kind: 'validation-failed' }
  | { kind: 'unavailable'; error: unknown }

// Login writes the browser-wide HttpOnly refresh cookie via Set-Cookie, so the
// whole round-trip runs inside the shared auth-cookie lock and uses the
// precommit protocol above. A new login is a new session boundary EVEN when
// the same account logs back in (NEXT is always a fresh marker). Failed
// credentials / validation roll the epoch back to OLD.
export async function login(request: LoginRequest): Promise<LoginOutcome> {
  try {
    return await withAuthCookieLock(async (): Promise<LoginOutcome> => {
      // 1. Read OLD. If it cannot be read we cannot safely roll back, so we
      //    fail closed BEFORE writing anything (ZERO POST /login).
      const old = readEpoch()
      if (old.status === 'unavailable') {
        return { kind: 'unavailable', error: old.error }
      }

      // 2. Precommit NEXT before the HTTP mutation. rotateEpoch throws if
      //    storage / secure randomness is unavailable → ZERO POST /login.
      let next: string
      try {
        next = rotateEpoch()
      } catch (error) {
        return { kind: 'unavailable', error }
      }

      // 3. HTTP mutation.
      let response: Response
      try {
        response = await httpRequest({
          path: AUTH_LOGIN_PATH,
          method: 'POST',
          json: request,
          credentials: 'include',
        })
      } catch (error) {
        // AMBIGUOUS after dispatch — the Set-Cookie may already have been
        // processed. KEEP NEXT; never roll back to OLD.
        return { kind: 'unavailable', error }
      }

      if (response.status === 200) {
        // Set-Cookie for the NEW session has been processed → NEXT is
        // COMMITTED. A later JSON parse failure keeps NEXT and never rolls
        // back — it would otherwise strand cookie=NEW / epoch=OLD.
        let data: LoginResponse
        try {
          data = (await response.json()) as LoginResponse
        } catch (error) {
          return { kind: 'unavailable', error }
        }
        return { kind: 'success', data, epoch: next }
      }

      // Rollback-eligible ONLY for statuses the backend proves leave the
      // cookie untouched: 401 (UnauthorizedWithoutBody) and 400
      // (ValidationProblem), both of which return before any Set-Cookie.
      if (response.status === 401 || response.status === 400) {
        if (!restoreEpoch(old)) {
          // Rollback write failed — do NOT advertise OLD as intact.
          return {
            kind: 'unavailable',
            error: new Error('login epoch rollback failed on a rollback-eligible status'),
          }
        }
        return response.status === 401
          ? { kind: 'invalid-credentials' }
          : { kind: 'validation-failed' }
      }

      // Any other status is NOT proven non-mutating (e.g. an ambiguous 500).
      // KEEP NEXT and fail closed.
      return {
        kind: 'unavailable',
        error: new Error(`Unexpected login status ${response.status}`),
      }
    })
  } catch (error) {
    // AuthLockUnavailableError / any unforeseen throw. Session unchanged.
    return { kind: 'unavailable', error }
  }
}

export type RefreshOutcome =
  | { kind: 'success'; data: RefreshResponse }
  | { kind: 'unauthenticated' }
  | { kind: 'unavailable'; error: unknown }

// ⚠️ LOW-LEVEL PRIMITIVE — DO NOT CALL DIRECTLY FROM PRODUCTION CODE.
//
// `authApi.refresh` deliberately does NOT acquire the shared auth-cookie
// lock. It is the raw network primitive that `singleFlightRefresh.refreshOnce`
// invokes INSIDE the lock it already holds. Adding lock acquisition here
// would nest the same Web Lock and deadlock the refresh path.
//
// All production callers (including future cold-bootstrap paths) must use
// `refreshOnce()` from `singleFlightRefresh`, which:
//   - acquires the shared cross-tab lock,
//   - re-checks the local session generation,
//   - calls this primitive once,
//   - re-checks the generation again,
//   - and stores the token behind the generation guard.
//
// Test files may call this primitive directly; production code must not.
export async function refresh(): Promise<RefreshOutcome> {
  try {
    const response = await httpRequest({
      path: AUTH_REFRESH_PATH,
      method: 'POST',
      credentials: 'include',
    })
    if (response.status === 200) {
      return { kind: 'success', data: (await response.json()) as RefreshResponse }
    }
    if (response.status === 401) return { kind: 'unauthenticated' }
    return {
      kind: 'unavailable',
      error: new Error(`Unexpected refresh status ${response.status}`),
    }
  } catch (error) {
    return { kind: 'unavailable', error }
  }
}

export type LogoutOutcome =
  | { kind: 'success' }
  | { kind: 'unavailable'; error: unknown }

// Logout deletes/revokes the same refresh-cookie state login and refresh
// mutate, so it too runs inside the shared auth-cookie lock and uses the same
// OLD → precommit NEXT → HTTP → keep/rollback protocol. On success it keeps
// NEXT (the marker change signals session removal to sibling tabs); on failure
// it rolls back OLD, failing closed if the rollback fails.
export async function logout(): Promise<LogoutOutcome> {
  try {
    return await withAuthCookieLock(async (): Promise<LogoutOutcome> => {
      // Read OLD only to fail closed if the epoch cannot be read at all.
      const old = readEpoch()
      if (old.status === 'unavailable') {
        return { kind: 'unavailable', error: old.error }
      }

      try {
        rotateEpoch() // precommit NEXT before the HTTP mutation
      } catch (error) {
        return { kind: 'unavailable', error }
      }

      let response: Response
      try {
        response = await httpRequest({
          path: AUTH_LOGOUT_PATH,
          method: 'POST',
          credentials: 'include',
        })
      } catch (error) {
        // AMBIGUOUS after dispatch — the Set-Cookie deletion may already have
        // been processed. KEEP NEXT; never roll back.
        return { kind: 'unavailable', error }
      }

      if (response.status === 204 || response.status === 200) {
        return { kind: 'success' } // keep NEXT
      }

      // Logout has NO deliberate non-mutating failure status, so no
      // post-dispatch outcome is rollback-eligible. KEEP NEXT and fail closed.
      return {
        kind: 'unavailable',
        error: new Error(`Unexpected logout status ${response.status}`),
      }
    })
  } catch (error) {
    // AuthLockUnavailableError / thrown fetch. Session unchanged.
    return { kind: 'unavailable', error }
  }
}

export type MeOutcome =
  | { kind: 'success'; data: LoginUserResponse }
  | { kind: 'unauthenticated' }
  | { kind: 'unavailable'; error: unknown }

// `/me` does not mutate the refresh cookie, so it deliberately does NOT
// acquire the auth-cookie lock. The caller must supply the access token
// explicitly. `authApi.me` is the RAW primitive: no bearer injection from a
// store, no refresh-and-retry. Callers that want retry-on-401 should use
// apiClient.
export async function me(accessToken: string): Promise<MeOutcome> {
  try {
    const response = await httpRequest({
      path: AUTH_ME_PATH,
      method: 'GET',
      headers: { Authorization: `Bearer ${accessToken}` },
    })
    if (response.status === 200) {
      return { kind: 'success', data: (await response.json()) as LoginUserResponse }
    }
    if (response.status === 401) return { kind: 'unauthenticated' }
    return {
      kind: 'unavailable',
      error: new Error(`Unexpected me status ${response.status}`),
    }
  } catch (error) {
    return { kind: 'unavailable', error }
  }
}
