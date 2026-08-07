// Cross-tab session marker. This is the ONE localStorage entry used anywhere
// in the auth module. It contains ONLY an opaque, non-secret UUID that
// identifies the current logical browser-wide session. Sibling tabs read
// this marker to detect when the session has been REPLACED (another tab
// logged in / out), and MUST refuse to silently continue an in-flight
// request against the new session.
//
// SECURITY BOUNDARY — the value stored under this key is:
//   - opaque (128 bits of secure randomness)
//   - non-secret (a MITM already sees /login and /refresh cookies)
//   - NOT a credential
//   - reveals ZERO information about who is logged in
//
// The value NEVER contains: access token, refresh token, userId,
// organizationId, email, roles, LoginResponse, or anything derived from
// them. Access tokens remain memory-only; the refresh token remains
// HttpOnly. This is an explicit and narrow exception to the module's
// "no localStorage for auth data" rule.
//
// The epoch rotates on:
//   - every successful login (a new login is a new session boundary, even
//     when the user logs into the same account)
//   - every successful logout
// The epoch does NOT rotate on a normal refresh — refresh rotates
// credentials for the SAME logical session. Exception: a cold-bootstrap
// refresh (when no epoch has been established yet) MAY write the initial
// value; that is establishment, not replacement.

export const SESSION_EPOCH_STORAGE_KEY = 'opsflow.auth.session-epoch'

// Thrown when the shared session-epoch storage cannot be used (localStorage
// absent / access throws / no secure randomness to mint a marker). Session
// replacement across tabs cannot be detected or recorded without it, so
// callers MUST fail closed rather than continuing an uncoordinated session
// transition. Wrapped errors preserve the underlying cause for diagnostics.
export class SessionEpochUnavailableError extends Error {
  readonly cause: unknown

  constructor(cause: unknown) {
    super(
      'Shared session-epoch storage is unavailable; cross-tab session-replacement detection cannot be guaranteed.',
    )
    this.name = 'SessionEpochUnavailableError'
    this.cause = cause
  }
}

// The three semantically distinct states of the shared epoch. `missing` and
// `unavailable` MUST NOT be collapsed to a single value: `missing` is a
// legitimate cold-initialization state, whereas `unavailable` means the
// safety invariant cannot be evaluated and automatic refresh/retry must fail
// closed. Two `unavailable` reads must never compare equal to each other.
export type EpochRead =
  | { status: 'present'; epoch: string }
  | { status: 'missing' }
  | { status: 'unavailable'; error: unknown }

// The originating epoch EXPECTATION a caller captured before its work began.
// Deliberately NOT `string | null`: `missing` is a concrete state that must
// NOT be reinterpreted as "whatever epoch exists later". A refresh keyed to
// `{ kind: 'missing' }` may proceed ONLY if the shared epoch is STILL missing
// when the lock is acquired; if an epoch has since appeared (a sibling
// login/logout) the refresh must be rejected as session-replaced.
// `unavailable` is a separate failure and never becomes an expectation.
export type ExpectedEpoch =
  | { kind: 'present'; epoch: string }
  | { kind: 'missing' }

export function sameExpectedEpoch(a: ExpectedEpoch, b: ExpectedEpoch): boolean {
  if (a.kind === 'present' && b.kind === 'present') return a.epoch === b.epoch
  return a.kind === b.kind // both 'missing'
}

// Reads the current shared epoch, distinguishing present / missing /
// unavailable. Never throws.
export function readEpoch(): EpochRead {
  try {
    if (typeof localStorage === 'undefined') {
      return { status: 'unavailable', error: new Error('localStorage is not available') }
    }
    const value = localStorage.getItem(SESSION_EPOCH_STORAGE_KEY)
    return value === null ? { status: 'missing' } : { status: 'present', epoch: value }
  } catch (error) {
    return { status: 'unavailable', error }
  }
}

// Storage write-availability is verified IMPLICITLY by the real epoch write
// (`writeEpoch`, used by `rotateEpoch`/`ensureEpoch`), which throws
// SessionEpochUnavailableError if `localStorage` is absent or `setItem`
// fails. Read-availability is surfaced by `readEpoch` returning
// `{ status: 'unavailable' }`. login/logout therefore fail closed on the
// actual OLD read + NEXT write without ever writing a separate probe key —
// the ONLY key this module ever touches is SESSION_EPOCH_STORAGE_KEY.

// Mints a fresh opaque marker using SECURE randomness. Collision resistance
// matters because equality means "same logical session", so we never fall
// back to Date.now()/Math.random(): if no secure RNG is available we fail
// closed with SessionEpochUnavailableError.
function generateEpoch(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID()
  }
  if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') {
    const bytes = new Uint8Array(16)
    crypto.getRandomValues(bytes)
    return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('')
  }
  throw new SessionEpochUnavailableError(
    new Error('No secure randomness (crypto.randomUUID / getRandomValues) available for the session epoch'),
  )
}

function writeEpoch(value: string): void {
  let store: Storage
  try {
    if (typeof localStorage === 'undefined') {
      throw new Error('localStorage is not available in this runtime')
    }
    store = localStorage
  } catch (error) {
    throw new SessionEpochUnavailableError(error)
  }
  try {
    store.setItem(SESSION_EPOCH_STORAGE_KEY, value)
  } catch (error) {
    throw new SessionEpochUnavailableError(error)
  }
}

// Ensures an epoch exists in shared storage. If one is present it is returned
// unchanged; if absent, a fresh secure marker is written and returned. Used
// by cold-bootstrap refresh to establish the initial marker. Throws
// SessionEpochUnavailableError if storage or secure randomness is unavailable.
export function ensureEpoch(): string {
  const read = readEpoch()
  if (read.status === 'present') return read.epoch
  if (read.status === 'unavailable') throw new SessionEpochUnavailableError(read.error)
  const fresh = generateEpoch()
  writeEpoch(fresh)
  return fresh
}

// Writes a fresh secure marker to shared storage and returns it. Used by the
// login/logout PRECOMMIT step (write NEXT before the HTTP mutation) and by a
// cold-bootstrap that has no marker yet. Throws SessionEpochUnavailableError
// if storage or secure randomness is unavailable.
export function rotateEpoch(): string {
  const fresh = generateEpoch()
  writeEpoch(fresh)
  return fresh
}

// Restores the shared marker to a PREVIOUS state — used to ROLL BACK a
// precommitted NEXT epoch when the login/logout HTTP mutation did not succeed.
// Returns whether the restore was applied:
//   - `present` → write the previous value back.
//   - `missing` → remove the key (there was no marker before).
//   - `unavailable` → cannot restore (the previous state was never known);
//     returns false so the caller fails closed and does NOT advertise OLD.
// Never throws; a storage failure during restore also returns false.
export function restoreEpoch(previous: EpochRead): boolean {
  if (previous.status === 'unavailable') return false
  try {
    if (typeof localStorage === 'undefined') return false
    if (previous.status === 'present') {
      localStorage.setItem(SESSION_EPOCH_STORAGE_KEY, previous.epoch)
    } else {
      localStorage.removeItem(SESSION_EPOCH_STORAGE_KEY)
    }
    return true
  } catch {
    return false
  }
}

// Test-only helper: clears the single marker key.
export function _resetSessionEpochForTests(): void {
  try {
    if (typeof localStorage !== 'undefined') {
      localStorage.removeItem(SESSION_EPOCH_STORAGE_KEY)
    }
  } catch {
    // Best-effort test cleanup.
  }
}
