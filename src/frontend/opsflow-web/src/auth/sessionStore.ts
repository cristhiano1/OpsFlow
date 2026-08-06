// In-memory store for the access token plus a monotonically increasing session
// generation used as an epoch guard. The generation exists so that a refresh
// that started before a logout (or a terminal auth failure) cannot restore an
// access token AFTER the frontend has decided the session is gone.
//
// The token is deliberately kept OUT of any Web Storage. Persistence across
// hard reloads is achieved by the HttpOnly refresh cookie plus a silent
// refresh on bootstrap, not by mirroring the access token in storage.

let accessToken: string | null = null
let generation = 0

export function getAccessToken(): string | null {
  return accessToken
}

// Sets the access token only if the caller's captured generation still matches
// the current generation. Returns whether the write was applied. A `false`
// return means the session was invalidated while the caller was mid-flight and
// the token must be discarded, not stored.
export function setAccessToken(token: string, capturedGeneration: number): boolean {
  if (capturedGeneration !== generation) {
    return false
  }
  accessToken = token
  return true
}

// Clears the in-memory token without touching the generation. Used when a
// specific operation wants to drop the token without declaring the whole
// session gone (rare; prefer `invalidateSession` for logout / session loss).
export function clearAccessToken(): void {
  accessToken = null
}

export function currentGeneration(): number {
  return generation
}

// Marks the current session as gone. Clears the token AND bumps the generation
// so any in-flight refresh whose result arrives later is rejected by
// `setAccessToken`. Returns the new generation for callers that want to log
// or correlate the transition.
export function invalidateSession(): number {
  accessToken = null
  generation += 1
  return generation
}

// Test-only helper: resets both module-scoped variables to their initial state.
// Prefixed with an underscore and exported so `beforeEach` blocks in Vitest
// can reset without any dynamic-import gymnastics.
export function _resetSessionStoreForTests(): void {
  accessToken = null
  generation = 0
}
