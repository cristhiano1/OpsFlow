// Low-level HTTP transport. Distinguishes JSON payloads from native fetch
// bodies so that future document/upload flows (FormData, Blob, streams) work
// without any JSON-shaped assumption:
//
//   - `json`   → JSON.stringify + auto Content-Type: application/json (unless
//                the caller supplied one, in any casing).
//   - `body`   → passed to fetch verbatim; Content-Type is NEVER auto-set,
//                because a FormData body needs the browser to supply the
//                multipart/form-data boundary automatically.
//
// `json` and `body` are mutually exclusive at runtime.
//
// This layer intentionally knows NOTHING about bearer tokens, 401 handling,
// or the refresh flow — those concerns live in apiClient.ts and
// singleFlightRefresh.ts. Both authApi (raw auth calls) and apiClient
// (bearer + refresh) are built on this single transport.

export interface HttpRequest {
  path: string
  method?: string
  // JSON payload. Will be JSON.stringify-ed and served with
  // `Content-Type: application/json` unless the caller already set one.
  json?: unknown
  // Native fetch body (FormData / Blob / string / URLSearchParams / etc.).
  // Passed to fetch verbatim; Content-Type is left to the caller (or, for
  // FormData, to the browser which supplies the multipart boundary).
  body?: BodyInit | null
  headers?: Record<string, string>
  credentials?: RequestCredentials
  signal?: AbortSignal
}

function hasHeaderCaseInsensitive(
  headers: Record<string, string>,
  target: string,
): boolean {
  const lower = target.toLowerCase()
  for (const name of Object.keys(headers)) {
    if (name.toLowerCase() === lower) return true
  }
  return false
}

export function httpRequest(request: HttpRequest): Promise<Response> {
  const method = request.method ?? 'GET'

  if (request.json !== undefined && request.body !== undefined) {
    throw new Error(
      'httpRequest: pass either `json` or `body`, not both — they are mutually exclusive.',
    )
  }

  const headers: Record<string, string> = { ...(request.headers ?? {}) }
  let body: BodyInit | null | undefined

  if (request.json !== undefined) {
    body = JSON.stringify(request.json)
    // HTTP header names are case-insensitive; a caller-supplied
    // `content-type` (any casing) must NOT be shadowed by a duplicate
    // `Content-Type` header.
    if (!hasHeaderCaseInsensitive(headers, 'content-type')) {
      headers['Content-Type'] = 'application/json'
    }
  } else {
    body = request.body ?? undefined
    // Deliberately NOT auto-setting Content-Type here: raw / FormData / Blob
    // bodies require the caller (or the browser, for FormData) to control it.
  }

  return fetch(request.path, {
    method,
    headers,
    body,
    credentials: request.credentials,
    signal: request.signal,
  })
}
