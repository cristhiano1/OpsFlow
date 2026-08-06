// Bearer-aware API client. Wraps every non-exempt call with:
//   1. Automatic Authorization: Bearer <access-token> injection sourced
//      exclusively from sessionStore. Any caller-supplied Authorization
//      header (in any casing) is stripped first — apiClient OWNS this header.
//   2. On 401, a single-flight refresh + exactly one retry with the fresh
//      token. If the retry itself returns 401, the session is invalidated
//      and the 401 is propagated — there is NO second refresh.
//   3. Late-401 race guard: if the store's token has changed between the
//      moment we captured our request's token and the moment its 401 arrives
//      (i.e. another concurrent request already refreshed the session), we
//      retry ONCE with the current token WITHOUT starting a second refresh.
//   4. Temporary refresh failure (network / server unavailability) does NOT
//      invalidate the session and does NOT return the original 401 as a
//      resolved Response — it REJECTS the promise with an
//      `ApiUnavailableError` so the caller can distinguish "backend said no"
//      from "backend not reachable".
//   5. Terminal-401 invalidation uses TOKEN IDENTITY, not just generation.
//      A retry that returns 401 only invalidates the session if the token
//      currently in the store is still exactly the token used by that retry;
//      otherwise a newer refresh already replaced it and must not be cleared.
//
// Only login / refresh / logout are exempt from the refresh-and-retry path so
// that they cannot recursively trigger themselves. Every other endpoint —
// including the authoritative /api/v1/auth/me — participates in the normal
// bearer + retry flow. `apiClient` imports the exempt path constants from
// `authApi`; the dependency graph is one-way (apiClient → authApi), so no
// cycle is introduced.

import { AUTH_LOGIN_PATH, AUTH_LOGOUT_PATH, AUTH_REFRESH_PATH } from './authApi'
import { httpRequest, type HttpRequest } from './httpClient'
import { getAccessToken, invalidateSession } from './sessionStore'
import { refreshOnce } from './singleFlightRefresh'

const AUTH_EXEMPT_PATHS: ReadonlySet<string> = new Set([
  AUTH_LOGIN_PATH,
  AUTH_REFRESH_PATH,
  AUTH_LOGOUT_PATH,
])

export type ApiRequest = HttpRequest

// Raised when a required refresh cannot complete for a non-authoritative
// reason (network failure, unexpected server error). Distinct from a 401
// Response so callers can distinguish "backend rejected the session" from
// "backend was not reachable". Session state is guaranteed unchanged when
// this is thrown.
export class ApiUnavailableError extends Error {
  readonly cause: unknown

  constructor(message: string, cause: unknown) {
    super(message)
    this.name = 'ApiUnavailableError'
    this.cause = cause
  }
}

interface Attempt {
  promise: Promise<Response>
  tokenUsed: string | null
}

// apiClient OWNS the Authorization header for its calls. Any caller-supplied
// value (in any casing) is discarded before the request goes out, so that a
// post-refresh retry is guaranteed to carry the current session token. Raw /
// custom HTTP authorization belongs in httpClient, never in apiClient.
function attemptWithCurrentToken(request: ApiRequest): Attempt {
  const token = getAccessToken()
  const headers: Record<string, string> = {}
  for (const [name, value] of Object.entries(request.headers ?? {})) {
    if (name.toLowerCase() !== 'authorization') {
      headers[name] = value
    }
  }
  if (token !== null) {
    headers.Authorization = `Bearer ${token}`
  }
  return {
    promise: httpRequest({ ...request, headers }),
    tokenUsed: token,
  }
}

// Terminal-401 invalidation guard: only clear the session if the store still
// holds the exact token that just failed. Prevents wiping a newer token that
// a later refresh cycle installed while our retry was in flight.
function maybeInvalidateForTerminal401(retryTokenUsed: string | null): void {
  if (retryTokenUsed !== null && getAccessToken() === retryTokenUsed) {
    invalidateSession()
  }
}

export async function apiFetch(request: ApiRequest): Promise<Response> {
  const isAuthExempt = AUTH_EXEMPT_PATHS.has(request.path)

  const first = attemptWithCurrentToken(request)
  const firstResponse = await first.promise

  if (firstResponse.status !== 401 || isAuthExempt) {
    return firstResponse
  }

  // Late-401 race: another concurrent request may have already refreshed the
  // session between the moment we captured `first.tokenUsed` and the moment
  // this 401 arrives. If so, skip our own refresh and retry once with the
  // current token.
  const tokenNowInStore = getAccessToken()
  if (tokenNowInStore !== null && tokenNowInStore !== first.tokenUsed) {
    const retry = attemptWithCurrentToken(request)
    const retryResponse = await retry.promise
    if (retryResponse.status === 401) {
      maybeInvalidateForTerminal401(retry.tokenUsed)
    }
    return retryResponse
  }

  if (tokenNowInStore === null && first.tokenUsed !== null) {
    // Session was invalidated between our attempt and now (logout or another
    // terminal path). Propagate the 401 without initiating a new refresh.
    return firstResponse
  }

  // Normal refresh-and-retry cycle.
  const refreshOutcome = await refreshOnce()

  if (refreshOutcome.kind === 'unavailable') {
    // Connectivity / unexpected server failure. Session state is untouched
    // (no clear, no generation bump). Surface a typed error so the caller
    // can render "temporarily unavailable" instead of "signed out".
    throw new ApiUnavailableError(
      'Session refresh could not be completed; session state unchanged.',
      refreshOutcome.error,
    )
  }

  if (refreshOutcome.kind === 'unauthenticated') {
    // Real 401 from /refresh — session is genuinely gone.
    maybeInvalidateForTerminal401(first.tokenUsed)
    return firstResponse
  }

  if (refreshOutcome.kind === 'stale') {
    // Someone else already invalidated / replaced the session — do NOT
    // invalidate again. Propagate the original 401 so the caller sees the
    // authoritative failure.
    return firstResponse
  }

  // refreshOutcome.kind === 'refreshed' — retry once with the fresh token.
  const retry = attemptWithCurrentToken(request)
  const retryResponse = await retry.promise

  if (retryResponse.status === 401) {
    // Retry with a fresh token still rejected — the backend has invalidated
    // the session (e.g. SecurityStamp rotated between refresh and retry).
    // Guarded by TOKEN IDENTITY so a newer replacement token is preserved.
    maybeInvalidateForTerminal401(retry.tokenUsed)
  }

  return retryResponse
}
