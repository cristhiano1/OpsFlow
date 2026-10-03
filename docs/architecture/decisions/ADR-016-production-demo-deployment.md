# ADR-016: Production demo deployment

## Status

Accepted

## Date

2026-10-03

## Context

OpsFlow needs a publicly accessible demo instance to showcase the platform's
capabilities. The deployment must be cost-effective, secure, and demonstrate the
full stack including the RAG pipeline. The demo is not a high-availability
production service — it is a portfolio demonstration running on a single VPS.

Key requirements:
- Free SQL Server licensing (no MSDN/paid subscription)
- Automatic TLS certificate provisioning
- Single-hop reverse proxy for API traffic
- Network isolation between public-facing and internal services
- Idempotent demo data seeding without running the API as Development

## Decision

### SQL Server Express edition

SQL Server 2025 Express is used for production (`MSSQL_PID=Express`). Runtime
verification confirmed that all OpsFlow features work on Express:

- `vector(1536)` data type
- `VECTOR_DISTANCE()` and `VECTOR_NORM()` functions
- Full-Text Search with `FREETEXTTABLE`
- All 9 EF Core migrations

Express limits (1.4 GB buffer pool, 4 cores, 50 GB database) are acceptable
for a demo instance and actually beneficial on a small VPS — they prevent SQL
Server from consuming all available memory.

### Proxy topology: Caddy → API directly

```
Internet → Caddy :443 → API :8080    (API traffic)
Internet → Caddy :443 → nginx :80    (frontend static files)
```

Caddy proxies `/api/*` and `/health/*` directly to the .NET API container.
nginx serves only frontend static files with SPA fallback. This is a single
proxy hop for API traffic, which simplifies:

- `ForwardedHeaders` configuration (`ForwardLimit=1`)
- Request attribution for rate limiting
- Latency (eliminates an extra hop)

The previous design (Caddy → nginx → API) was rejected because the second hop
adds complexity and latency with no benefit.

### Network isolation

Two Docker networks with explicit IPAM enforce the principle of least
connectivity:

| Network | Subnet | Services | Internet access |
|---|---|---|---|
| `opsflow-public` | `172.30.10.0/24` | Caddy (`172.30.10.10`), API, nginx | Yes (via Caddy) |
| `opsflow-database` | Docker-assigned | API, SQL Server | No (internal) |

Only Caddy publishes ports (80, 443). SQL Server is reachable only by the API.
The public network uses a pinned IPAM subnet (`172.30.10.0/24`) and Caddy is
assigned a deterministic IP (`172.30.10.10`) so the API's ForwardedHeaders
middleware trusts only that specific address.

### Demo seeding

A new `SEED_DEMO_DATA` environment variable triggers demo account creation in
Production mode. This reuses the existing idempotent `DevelopmentDataSeeder`
but is gated separately from Development seeding:

- `Seed:Enabled` — controls Development-mode seeding (unchanged)
- `SEED_DEMO_DATA` — controls Production demo seeding (new)

The API runs as `ASPNETCORE_ENVIRONMENT=Production` in the demo — it is not
run as Development.

### ForwardedHeaders

The API trusts `X-Forwarded-For` and `X-Forwarded-Proto` from Caddy's
deterministic IP (`172.30.10.10`) with `ForwardLimit=1`. The trusted proxy
is configurable via `ForwardedHeaders:TrustedProxy` (a single IP address).
Using `KnownProxies` with a single IP is narrower than trusting a CIDR range,
blocking `X-Forwarded-For` spoofing from any other container on the network.
This middleware runs only in non-Development environments and is placed first
in the pipeline.

### Image pinning

Production services use GHCR images tagged with the exact commit SHA
(`sha-<8chars>`). The deploy workflow builds, pushes, and exports the
immutable image references (`OPSFLOW_API_IMAGE`, `OPSFLOW_WEB_IMAGE`) to the
remote host. There is no `latest` tag dependency for deployment correctness —
every deployment is traceable to a specific commit.

### Deployment gate

Deployment uses `workflow_dispatch` (manual trigger) rather than automatic
deployment on push to main. This provides a human gate before any production
change.

## Consequences

- Express edition is free for production use; no licensing cost
- Single-hop proxy reduces configuration complexity and latency
- Network isolation with pinned IPAM prevents direct database access and
  enables deterministic proxy trust
- Deterministic Caddy IP (`172.30.10.10`) narrows ForwardedHeaders trust to a
  single address, blocking X-Forwarded-For spoofing from other containers
- Commit-SHA image tags ensure every deployment is traceable and reproducible
- Demo seeding is explicit and separate from development seeding
- Manual deployment gate prevents accidental production changes
- All services restart automatically after crashes or host reboots
- TLS is automatic via Caddy's ACME integration (Let's Encrypt)
- Docker Compose >= 2.24.4 is required for `!override` tag support
