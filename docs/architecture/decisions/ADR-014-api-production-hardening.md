# ADR-014: API Production Hardening

**Status:** Accepted
**Date:** 2026-10-02

## Context

OpsFlow lacked three standard production-readiness features: health check
endpoints for orchestrator probes, explicit CORS configuration for
cross-origin frontend deployment, and rate limiting to protect
abuse-sensitive and resource-intensive endpoints. All three gaps were
documented in the README limitations.

## Decision

### Health checks

Two health check endpoints are registered via ASP.NET Core's built-in
health check infrastructure:

| Endpoint | Purpose | Checks |
|---|---|---|
| `GET /health/live` | Liveness — confirms the process is running | None (always healthy) |
| `GET /health/ready` | Readiness — confirms the application can serve requests | SQL Server connectivity |

Both endpoints are anonymous and return only the aggregate status string
(`Healthy` / `Unhealthy`). No connection strings, exception details, or
server names are exposed. The readiness check uses `CanConnectAsync` on
the `OpsFlowDbContext` — a fast, read-only connectivity test.

Optional external providers (OpenAI, AWS Bedrock) are excluded from
readiness because they are config-gated and their unavailability should
not prevent the API from serving non-AI requests.

### CORS

An explicit CORS policy (`OpsFlowCors`) is registered with an origin
allowlist read from `Cors:AllowedOrigins` configuration. The development
default includes `http://localhost:5173` (the Vite dev server).

| Setting | Value | Rationale |
|---|---|---|
| Origins | Configuration-driven allowlist | No `AllowAnyOrigin`; secure default when unconfigured |
| Methods | GET, POST | Only methods the current API uses |
| Headers | Content-Type, Authorization | Authorization needed for JWT bearer tokens |
| Credentials | Allowed for trusted origins | Login, refresh, and logout use an HttpOnly refresh cookie and the frontend sends `credentials: 'include'`; explicit origins are required (never `*`) |

If no origins are configured, the CORS policy allows nothing — a secure
default for production environments that haven't completed CORS setup.

**Limitation:** the refresh cookie remains `SameSite=Strict`. Credentialed CORS
therefore supports same-site cross-origin deployments (for example sibling
subdomains or different ports where the browser considers the sites the same),
but not a frontend hosted on a different site. Supporting cross-site refresh
would require a separate CSRF-safe cookie/session design.

### Rate limiting

Four named policies are registered using ASP.NET Core's built-in rate
limiting middleware with fixed-window limiters:

| Policy | Default Limit | Window | Applied To |
|---|---|---|---|
| `AuthStrict` | 10 req | 60 s | Login, refresh, logout |
| `ApiStandard` | 60 req | 60 s | Project/document reads, `/me` |
| `RagExpensive` | 10 req | 60 s | Search, answer |
| `Upload` | 10 req | 60 s | Document upload |

Health endpoints have no rate-limiting policy, so orchestrator probes
are never throttled.

**Partition strategy:**

- `AuthStrict`: partitioned by client IP address, since the caller is
  not yet authenticated.
- All other policies: partitioned by the authenticated user's `sub`
  claim when available, falling back to client IP address for
  unauthenticated requests.

The partition key uses `RemoteIpAddress` directly. Without forwarded-
header middleware, this is the immediate TCP peer — accurate for direct
connections, but behind a reverse proxy it would be the proxy's IP.

**Rejected request behavior:** 429 Too Many Requests with a `Retry-After`
header (provided automatically by the fixed-window limiter).

### Middleware order

```
(non-Development) UseExceptionHandler
(non-Development) UseHttpsRedirection
UseCors
UseAuthentication
UseRateLimiter         ← after auth so user claims are available
UseAuthorization
MapHealthChecks        ← /health/live, /health/ready (anonymous)
MapControllers
```

CORS runs before authentication because the browser sends preflight
OPTIONS requests without credentials. Rate limiting runs after
authentication so that authenticated-endpoint policies can partition by
user ID rather than IP.

## Alternatives considered

- **Redis-backed distributed rate limiting:** Rejected as premature —
  the application currently runs as a single instance.
- **Global rate limiter:** Rejected to avoid throttling health probes
  and to allow distinct limits per endpoint type.
- **Sliding window or token bucket:** Rejected in favor of fixed window
  for simplicity; the current endpoint mix does not need burst
  allowance.
- **Omitting CORS credentials:** Rejected because login, refresh, and logout
  use an HttpOnly refresh cookie and the frontend sends `credentials: 'include'`.
  Credentials are enabled only for explicitly allowlisted origins.

## Consequences

- Orchestrators can probe liveness and readiness separately.
- Cross-origin frontend deployments are supported with explicit origin
  allowlisting.
- Authentication brute force is rate-limited to 10 attempts per minute
  per IP.
- Expensive AI/RAG endpoints are throttled to 10 requests per minute
  per user.
- All limits are configurable via `appsettings.json` or environment
  variables without code changes.
- Behind a reverse proxy, `X-Forwarded-For` header processing must be
  configured separately for IP-based partitioning to be accurate.
