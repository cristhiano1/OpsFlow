// Bearer-aware API client. Wraps every non-exempt call with:
//   1. Automatic Authorization: Bearer <access-token> injection sourced
//      exclusively from sessionStore. Any caller-supplied Authorization
//      header (in any casing) is stripped first — apiClient OWNS this header.
//   2. On 401, a single-flight refresh + exactly one retry with the fresh
//      token. If the retry itself returns 401, the session is invalidated
//      and the 401 is propagated — there is NO second refresh.
//   3. Late-401 race guard: retry with the current token WITHOUT a second
//      refresh — but ONLY if the originating session is still current (#7).
//   4. Temporary refresh failure (network / server / storage unavailability)
//      does NOT invalidate the session and does NOT return the original 401 as
//      a resolved Response — it REJECTS with `ApiUnavailableError` so callers
//      can distinguish "backend said no" from "cannot be evaluated".
//   5. Terminal-401 invalidation uses TOKEN IDENTITY, not just generation.
//   6. Request bodies are constrained to the REPLAYABLE subset of BodyInit
//      (`XMLHttpRequestBodyInit`); ReadableStream is excluded at type + runtime.
//   7. CROSS-TAB SESSION-REPLACEMENT guard. BEFORE dispatching its first HTTP
//      attempt, every non-exempt request reads the shared epoch and snapshots
//      the local session (token + principal + BOUND epoch). Two gates:
//        (a) PRE-DISPATCH: if this tab holds a token whose bound epoch no
//            longer equals the (readable) shared epoch (a sibling already
//            replaced the session), throw a typed SessionReplacedError with
//            ZERO fetch — never a fabricated backend 401.
//        (b) IN-FLIGHT: if the session is replaced AFTER dispatch, the
//            late-401 / post-refresh retry is blocked because the originating
//            epoch/principal no longer match.
//      If the shared epoch cannot be READ, the whole request is rejected up
//      front and NOTHING is dispatched — a conservative fail-closed. Capturing
//      before the first attempt is essential: a sibling may replace the
//      session while the request is in flight, so checking only after the 401
//      would be too late.
//
// Only login / refresh / logout are exempt from the refresh-and-retry path so
// that they cannot recursively trigger themselves. Every other endpoint —
// including the authoritative /api/v1/auth/me — participates in the normal
// bearer + retry flow.

import { AUTH_LOGIN_PATH, AUTH_LOGOUT_PATH, AUTH_REFRESH_PATH } from './authApi'
import { httpRequest, type HttpRequest } from './httpClient'
import { type ExpectedEpoch, readEpoch } from './sessionEpoch'
import {
  getAccessToken,
  getBoundEpoch,
  getPrincipal,
  getSession,
  invalidateSession,
  samePrincipal,
  type Principal,
} from './sessionStore'
import { refreshOnce } from './singleFlightRefresh'

const AUTH_EXEMPT_PATHS: ReadonlySet<string> = new Set([
  AUTH_LOGIN_PATH,
  AUTH_REFRESH_PATH,
  AUTH_LOGOUT_PATH,
])

// apiClient re-declares body explicitly as `XMLHttpRequestBodyInit` (the
// non-streaming replayable subset). Kept intentionally so the
// authenticated-retry contract remains self-documenting; `ReadableStream`
// is excluded at both layers.
export interface ApiRequest extends Omit<HttpRequest, 'body'> {
  body?: XMLHttpRequestBodyInit | null
}

// Raised when a required refresh cannot complete — or the cross-tab safety
// invariant cannot be evaluated — for a non-authoritative reason (network
// failure, unexpected server error, cross-tab lock / epoch storage
// unavailable). Distinct from a 401 Response. Session state is guaranteed
// unchanged when this is thrown.
export class ApiUnavailableError extends Error {
  readonly cause: unknown

  constructor(message: string, cause: unknown) {
    super(message)
    this.name = 'ApiUnavailableError'
    this.cause = cause
  }
}

// Raised when the cross-tab session-replacement invariant is SUCCESSFULLY
// evaluated and found violated BEFORE dispatch — the local authenticated
// session's bound epoch no longer equals the (readable) shared epoch, i.e. a
// sibling tab replaced the session. Distinct from ApiUnavailableError (which
// means the invariant could NOT be evaluated). apiClient throws this instead
// of fabricating a backend 401 the server never produced. Throwing it mutates
// no state — the sibling's session is left untouched.
export class SessionReplacedError extends Error {
  constructor() {
    super('The local session was replaced by another tab before this request was dispatched.')
    this.name = 'SessionReplacedError'
  }
}

// The immutable identity of the logical session that originated a request,
// captured BEFORE any HTTP attempt is dispatched. `expected` records whether
// an epoch existed at capture (`present`) or not (`missing`) — a `missing`
// origin must NEVER adopt an epoch that appeared afterwards.
interface OriginatingSession {
  token: string | null
  principal: Principal | null
  expected: ExpectedEpoch
}

interface Attempt {
  promise: Promise<Response>
  tokenUsed: string | null
}

// Dispatches the request with an EXPLICIT token (never re-reading the store),
// stripping any caller-supplied Authorization header (in any casing) so the
// bearer is fully owned by apiClient.
function attemptWithToken(request: ApiRequest, token: string | null): Attempt {
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

// True when the session that originated the request is STILL current. If the
// epoch cannot be read now, or a `missing`-origin request now sees an epoch,
// we conservatively treat the session as no longer current (block the retry).
function originatingSessionStillCurrent(origin: OriginatingSession): boolean {
  const read = readEpoch()
  if (origin.expected.kind === 'present') {
    if (read.status !== 'present' || read.epoch !== origin.expected.epoch) {
      return false
    }
  } else {
    // A `missing`-origin request is only still current while the epoch is
    // STILL missing — an epoch that appeared means the session was replaced.
    if (read.status !== 'missing') {
      return false
    }
  }
  if (origin.principal !== null && !samePrincipal(getPrincipal(), origin.principal)) {
    return false
  }
  return true
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
  // Belt-and-suspenders runtime guard against one-shot ReadableStream bodies.
  if (
    typeof ReadableStream !== 'undefined'
    && (request.body as unknown) instanceof ReadableStream
  ) {
    throw new Error(
      'apiClient does not accept ReadableStream bodies — bodies must be replayable so a post-refresh retry can safely re-send them.',
    )
  }

  const isAuthExempt = AUTH_EXEMPT_PATHS.has(request.path)

  if (isAuthExempt) {
    // Exempt endpoints never participate in refresh-and-retry. Inject the
    // current bearer and pass through.
    return await attemptWithToken(request, getAccessToken()).promise
  }

  // ── Capture the originating session BEFORE any fetch is dispatched. ──
  // Order is load-bearing: read the shared epoch first, then snapshot the
  // local session, THEN dispatch. If the epoch is unreadable we fail closed
  // up front and dispatch NOTHING — no request, no late-401 path, no refresh.
  const epochRead = readEpoch()
  if (epochRead.status === 'unavailable') {
    throw new ApiUnavailableError(
      'Shared session epoch is unreadable; cannot evaluate cross-tab session safety.',
      epochRead.error,
    )
  }
  const session = getSession()

  // Pre-dispatch bound-epoch check: if this tab holds an authenticated
  // session, its token must be BOUND to the CURRENT shared epoch. The epoch was
  // read successfully above (unavailable already threw ApiUnavailableError), so
  // a mismatch here is an AUTHORITATIVELY-EVALUATED session replacement (the
  // marker moved, or is `missing`). Reject with a typed SessionReplacedError
  // and ZERO fetch — never fabricate a 401.
  if (session.token !== null) {
    const boundStillCurrent =
      epochRead.status === 'present'
      && session.boundEpoch !== null
      && epochRead.epoch === session.boundEpoch
    if (!boundStillCurrent) {
      throw new SessionReplacedError()
    }
  }

  // Capture the epoch EXPECTATION exactly as it stands now. `missing` is
  // preserved as a distinct state so refreshOnce cannot later adopt an epoch
  // that a sibling creates after this request began.
  const expected: ExpectedEpoch = epochRead.status === 'present'
    ? { kind: 'present', epoch: epochRead.epoch }
    : { kind: 'missing' }
  const origin: OriginatingSession = {
    token: session.token,
    principal: session.principal,
    expected,
  }

  // Only now dispatch, using the captured token.
  const first = attemptWithToken(request, origin.token)
  const firstResponse = await first.promise

  if (firstResponse.status !== 401) {
    return firstResponse
  }

  // Late-401 race: another concurrent request may have refreshed the session
  // between capture and this 401. Retry with the current token ONLY if the
  // originating session is still current — a token change alone no longer
  // proves "same session refreshed"; it may mean the session changed.
  const tokenNowInStore = getAccessToken()
  if (tokenNowInStore !== null && tokenNowInStore !== origin.token) {
    if (!originatingSessionStillCurrent(origin)) {
      return firstResponse
    }
    const retry = attemptWithToken(request, tokenNowInStore)
    const retryResponse = await retry.promise
    if (retryResponse.status === 401) {
      maybeInvalidateForTerminal401(retry.tokenUsed)
    }
    return retryResponse
  }

  if (tokenNowInStore === null && origin.token !== null) {
    // Session was invalidated between our attempt and now (logout or another
    // terminal path). Propagate the 401 without initiating a new refresh.
    return firstResponse
  }

  // Normal refresh-and-retry cycle. Pass the captured epoch EXPECTATION AND
  // principal so refreshOnce rejects — before installing anything — a refresh
  // that would adopt a sibling-created/replaced epoch or a different account.
  const refreshOutcome = await refreshOnce(origin.expected, origin.principal)

  if (refreshOutcome.kind === 'unavailable') {
    throw new ApiUnavailableError(
      'Session refresh could not be completed; session state unchanged.',
      refreshOutcome.error,
    )
  }

  if (refreshOutcome.kind === 'unauthenticated') {
    maybeInvalidateForTerminal401(origin.token)
    return firstResponse
  }

  if (refreshOutcome.kind === 'stale' || refreshOutcome.kind === 'session-replaced') {
    // A LOCAL logout ('stale') or a CROSS-TAB session replacement / principal
    // mismatch ('session-replaced') superseded this request. refreshOnce did
    // NOT install any foreign token. Do NOT invalidate and do NOT retry.
    return firstResponse
  }

  // refreshOutcome.kind === 'refreshed'. refreshOnce already enforced the
  // epoch expectation and the principal guard BEFORE installing. Belt-and-
  // suspenders before replaying: the epoch under which refresh ACTUALLY
  // succeeded must still be current in BOTH shared storage AND the local
  // session. This closes the post-refresh race for ALL origins — including
  // a `missing` origin that legitimately established E_new — because a
  // sibling can rotate the shared epoch between refreshOnce returning and
  // the retry below.
  const refreshedPrincipal: Principal = {
    userId: refreshOutcome.data.user.userId,
    organizationId: refreshOutcome.data.user.organizationId,
  }
  const principalMatches =
    origin.principal === null || samePrincipal(refreshedPrincipal, origin.principal)
  const postRefreshRead = readEpoch()
  const epochStillCurrent =
    postRefreshRead.status === 'present'
    && postRefreshRead.epoch === refreshOutcome.sessionEpoch
    && getBoundEpoch() === refreshOutcome.sessionEpoch
  if (!principalMatches || !epochStillCurrent) {
    throw new SessionReplacedError()
  }

  const retry = attemptWithToken(request, getAccessToken())
  const retryResponse = await retry.promise

  if (retryResponse.status === 401) {
    maybeInvalidateForTerminal401(retry.tokenUsed)
  }

  return retryResponse
}
