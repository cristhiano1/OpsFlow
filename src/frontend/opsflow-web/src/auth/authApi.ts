// Raw authentication API. Every function goes through the low-level
// httpClient and NEVER through apiClient's automatic-refresh layer, so that:
//   - login / refresh / logout cannot recursively trigger themselves;
//   - the bootstrap and single-flight refresh paths always see the API's
//     real 401, not a synthesised retry outcome.
//
// Each function returns a discriminated union so callers can pattern-match on
// outcomes without try/catch around every call. The `unavailable` variant
// covers ANY non-authoritative failure (network error, unexpected server
// response, thrown fetch) — that is, situations in which the frontend cannot
// treat the outcome as "the backend said no" and therefore must NOT
// invalidate the session.

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

export async function login(request: LoginRequest): Promise<LoginOutcome> {
  try {
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
    return { kind: 'unavailable', error: new Error(`Unexpected login status ${response.status}`) }
  } catch (error) {
    return { kind: 'unavailable', error }
  }
}

export type RefreshOutcome =
  | { kind: 'success'; data: RefreshResponse }
  | { kind: 'unauthenticated' }
  | { kind: 'unavailable'; error: unknown }

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
    return { kind: 'unavailable', error: new Error(`Unexpected refresh status ${response.status}`) }
  } catch (error) {
    return { kind: 'unavailable', error }
  }
}

export type LogoutOutcome =
  | { kind: 'success' }
  | { kind: 'unavailable'; error: unknown }

export async function logout(): Promise<LogoutOutcome> {
  try {
    const response = await httpRequest({
      path: AUTH_LOGOUT_PATH,
      method: 'POST',
      credentials: 'include',
    })
    if (response.status === 204 || response.status === 200) return { kind: 'success' }
    return { kind: 'unavailable', error: new Error(`Unexpected logout status ${response.status}`) }
  } catch (error) {
    return { kind: 'unavailable', error }
  }
}

export type MeOutcome =
  | { kind: 'success'; data: LoginUserResponse }
  | { kind: 'unauthenticated' }
  | { kind: 'unavailable'; error: unknown }

// The caller must supply the access token explicitly. authApi.me is the RAW
// primitive: no bearer injection from a store, no refresh-and-retry. Callers
// that want retry-on-401 should use apiClient.
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
    return { kind: 'unavailable', error: new Error(`Unexpected me status ${response.status}`) }
  } catch (error) {
    return { kind: 'unavailable', error }
  }
}
