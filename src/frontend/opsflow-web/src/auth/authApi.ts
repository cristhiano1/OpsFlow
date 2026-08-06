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
// Each function returns a discriminated union so callers can pattern-match
// on outcomes without try/catch around every call. The `unavailable`
// variant covers ANY non-authoritative failure (network error, unexpected
// server response, thrown fetch, or cross-tab lock unavailability) — that
// is, situations in which the frontend cannot treat the outcome as "the
// backend said no" and therefore must NOT invalidate the session.

import { withAuthCookieLock } from './authCookieLock'
import { httpRequest } from './httpClient'
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
  | { kind: 'success'; data: LoginResponse }
  | { kind: 'invalid-credentials' }
  | { kind: 'validation-failed' }
  | { kind: 'unavailable'; error: unknown }

// Login writes the browser-wide HttpOnly refresh cookie via Set-Cookie, so
// the entire HTTP round-trip runs inside the shared auth-cookie lock. Any
// concurrent refresh / logout waits for this lock, and vice versa, so the
// Set-Cookie responses cannot race.
export async function login(request: LoginRequest): Promise<LoginOutcome> {
  try {
    return await withAuthCookieLock(async (): Promise<LoginOutcome> => {
      const response = await httpRequest({
        path: AUTH_LOGIN_PATH,
        method: 'POST',
        json: request,
        credentials: 'include',
      })
      if (response.status === 200) {
        return { kind: 'success', data: (await response.json()) as LoginResponse }
      }
      if (response.status === 401) return { kind: 'invalid-credentials' }
      if (response.status === 400) return { kind: 'validation-failed' }
      return {
        kind: 'unavailable',
        error: new Error(`Unexpected login status ${response.status}`),
      }
    })
  } catch (error) {
    // Covers both `AuthLockUnavailableError` (lock unavailable → zero POST)
    // and any thrown fetch (network error). Session state is unchanged.
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
// mutate, so it too runs inside the shared auth-cookie lock. If the lock is
// unavailable we return `unavailable` and issue ZERO POST /logout requests;
// the caller decides how to reconcile local session state.
export async function logout(): Promise<LogoutOutcome> {
  try {
    return await withAuthCookieLock(async (): Promise<LogoutOutcome> => {
      const response = await httpRequest({
        path: AUTH_LOGOUT_PATH,
        method: 'POST',
        credentials: 'include',
      })
      if (response.status === 204 || response.status === 200) {
        return { kind: 'success' }
      }
      return {
        kind: 'unavailable',
        error: new Error(`Unexpected logout status ${response.status}`),
      }
    })
  } catch (error) {
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
