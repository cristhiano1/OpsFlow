// In-memory store for the LOCAL session: the access token, the PRINCIPAL that
// token belongs to, the shared session EPOCH the token is bound to, and a
// monotonically increasing LOCAL generation.
//
// Two distinct cross-cutting concepts:
//
//   1. LOCAL generation (this module): tab-local. Protects against stale
//      async work INSIDE this tab — a refresh that started before a local
//      logout must not restore a token afterwards. NOT a cross-tab signal.
//
//   2. SHARED session epoch (see sessionEpoch.ts): browser-wide via
//      localStorage. Protects against session REPLACEMENT by ANOTHER tab.
//      The store records which epoch the current token is BOUND to, so a
//      request can be rejected before dispatch when the local bound epoch no
//      longer equals the shared epoch (a sibling replaced the session).
//
// token + principal + boundEpoch are installed and cleared ATOMICALLY
// (`setSession` / `invalidateSession`) so the store can never be observed in
// a torn state — e.g. a token from session B while the principal or bound
// epoch still says session A.
//
// The token is deliberately kept OUT of any Web Storage. Persistence across
// hard reloads is achieved by the HttpOnly refresh cookie plus a silent
// refresh on bootstrap, not by mirroring the access token in storage.

// The stable principal identity a token is bound to. Deliberately only the
// authoritative id fields — never email / displayName (those are mutable
// display data, not identity).
export interface Principal {
  userId: string
  organizationId: string
}

// An atomic snapshot of the local session.
export interface SessionSnapshot {
  token: string | null
  principal: Principal | null
  boundEpoch: string | null
}

export function samePrincipal(a: Principal | null, b: Principal | null): boolean {
  if (a === null || b === null) return a === b
  return a.userId === b.userId && a.organizationId === b.organizationId
}

let accessToken: string | null = null
let principal: Principal | null = null
let boundEpoch: string | null = null
let generation = 0
const invalidationListeners = new Set<() => void>()

export function getAccessToken(): string | null {
  return accessToken
}

export function getPrincipal(): Principal | null {
  return principal
}

export function getBoundEpoch(): string | null {
  return boundEpoch
}

// Reads the whole local session as one consistent snapshot. No `await`
// separates the field reads, so callers observe token + principal + boundEpoch
// from the same point in time.
export function getSession(): SessionSnapshot {
  return { token: accessToken, principal, boundEpoch }
}

// Atomically installs the access token, its principal, AND the shared epoch it
// is bound to — but only if the caller's captured generation still matches the
// current generation. Returns whether the write was applied. A `false` return
// means the session was invalidated (local logout / terminal failure) while
// the caller was mid-flight and ALL THREE fields must be discarded, not stored.
export function setSession(
  token: string,
  tokenPrincipal: Principal,
  tokenBoundEpoch: string,
  capturedGeneration: number,
): boolean {
  if (capturedGeneration !== generation) {
    return false
  }
  accessToken = token
  principal = tokenPrincipal
  boundEpoch = tokenBoundEpoch
  return true
}

// Clears the in-memory session (token + principal + boundEpoch) without
// bumping the generation. Rare; prefer `invalidateSession`.
export function clearAccessToken(): void {
  accessToken = null
  principal = null
  boundEpoch = null
}

export function currentGeneration(): number {
  return generation
}

export function subscribeSessionInvalidation(listener: () => void): () => void {
  invalidationListeners.add(listener)
  return () => { invalidationListeners.delete(listener) }
}

// Marks the current LOCAL session as gone. Clears all three fields AND bumps
// the generation so any in-flight refresh whose result arrives later is
// rejected by `setSession`. Notifies invalidation subscribers synchronously
// after the state change so React can observe the invalidation even when the
// shared epoch does not change (no cross-tab storage event). Returns the new
// generation.
export function invalidateSession(): number {
  accessToken = null
  principal = null
  boundEpoch = null
  generation += 1
  for (const listener of [...invalidationListeners]) {
    try {
      listener()
    } catch {
      // Subscriber failure must not break session invalidation.
    }
  }
  return generation
}

// Test-only helper: resets all module-scoped state to initial.
export function _resetSessionStoreForTests(): void {
  accessToken = null
  principal = null
  boundEpoch = null
  generation = 0
  invalidationListeners.clear()
}
