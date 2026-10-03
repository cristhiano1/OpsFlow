# ADR-015: Application Containerization

**Status:** Accepted
**Date:** 2026-10-03

## Context

OpsFlow's `docker-compose.yml` provided only the SQL Server database
(Phase 0). The backend and frontend were started manually via
`start-opsflow.ps1` or separate terminal commands. The README listed
"No application Dockerfiles" as a limitation.

A containerized stack enables reproducible local environments, removes
per-machine toolchain requirements beyond Docker, and establishes the
image artifacts that any future deployment strategy would consume.

## Decision

### Backend image (`src/backend/Dockerfile`)

Multi-stage build with three stages:

| Stage | Base | Purpose |
|---|---|---|
| `restore` | `mcr.microsoft.com/dotnet/sdk:10.0` | Copy `.csproj` files and restore NuGet packages |
| `build` | (from restore) | Copy source, `dotnet publish --configuration Release` |
| `runtime` | `mcr.microsoft.com/dotnet/aspnet:10.0` | Published output only; no SDK, no source, no tests |

The runtime image:
- Runs as a non-root user (`opsflow`, UID 10001)
- Listens on `http://+:8080` via `ASPNETCORE_URLS`
- Receives all configuration through environment variables
- Includes `curl` for the Docker health check probe
- Does not contain `global.json` (the Docker SDK version is compatible
  with `net10.0`; the local `global.json` pins a specific SDK patch for
  developer machines only)

### Frontend image (`src/frontend/Dockerfile`)

Multi-stage build:

| Stage | Base | Purpose |
|---|---|---|
| `build` | `node:22-alpine` | `npm ci` + `npm run build` (Vite production build) |
| `runtime` | `nginx:stable-alpine` | Serves static assets, proxies `/api` to the backend |

No development server (`vite dev`) runs in the production container.

### Same-origin reverse proxy

The nginx configuration (`docker/nginx/default.conf`) serves the SPA
and reverse-proxies `/api/*` and `/health/*` to the backend container.
This preserves the same-origin topology that the authentication flow
requires:

- The refresh cookie is `HttpOnly`, `Secure`, `SameSite=Strict`,
  `Path=/api/v1/auth`
- The frontend uses `credentials: 'include'` on auth requests
- Both the SPA and the API are served from the same origin
  (`http://localhost:3000`), so the browser treats cookie operations
  as same-origin

No changes to `SameSite`, `Secure`, or CORS configuration were needed.

### SQL Server dependency

The existing SQL Server 2025 image with Full-Text Search
(`docker/sqlserver-fts/Dockerfile`) is preserved. The API container
connects via the Docker service hostname (`sqlserver`) and the internal
port (`1433`), not the published host port.

### Document storage persistence

Uploaded documents are stored in `/app/storage` inside the API
container, backed by a named Docker volume (`opsflow-documents`). The
`DocumentStorage:BasePath` configuration is set to `/app/storage` via
environment variable, overriding the development-relative default.

### Migration strategy

A new opt-in `APPLY_MIGRATIONS` environment variable (default `true` in
`docker-compose.yml`) runs `Database.MigrateAsync()` at API startup.
This is a separate code path from the existing development seeding:

- `APPLY_MIGRATIONS=true`: runs **only** `MigrateAsync()` — schema
  changes, no seed data
- Development seeding remains gated on `ASPNETCORE_ENVIRONMENT=Development`
  **and** `Seed:Enabled=true`, unchanged from before

The container runs with `ASPNETCORE_ENVIRONMENT=Production` and
`Seed:Enabled=false`, so no demo users or seed data are created.

### Secret injection

All secrets are injected via environment variables in
`docker-compose.yml`, substituted from the `.env` file:

| Secret | Variable |
|---|---|
| SQL Server SA password | `MSSQL_SA_PASSWORD` |
| JWT signing key | `JWT_SIGNING_KEY` |
| OpenAI API key | `OpenAI__ApiKey` (optional) |

No secrets are baked into Docker images. The `.env.example` contains
safe placeholder values for local development.

## Alternatives considered

- **Kubernetes manifests:** Rejected as premature — the application runs
  as a single-instance local stack. Container images are
  Kubernetes-ready when the time comes.
- **Separate migration container/init job:** Rejected in favor of the
  simpler opt-in startup flag. A dedicated migration container adds
  orchestration complexity without benefit for a single-instance setup.
- **`vite preview` in the frontend container:** Rejected in favor of
  nginx, which provides reverse proxy capability, efficient static file
  serving, and proven production characteristics.
- **Traefik/Caddy as the reverse proxy:** Rejected — nginx is simpler
  for the current single-service-behind-proxy topology.

## Consequences

- `docker compose up -d --build` starts the complete OpsFlow stack.
- Frontend, API, and database run as isolated containers with defined
  dependencies and health checks.
- The same-origin authentication flow works without configuration
  changes.
- Uploaded documents persist across container restarts via a named
  volume.
- Database schema is applied automatically on first startup.
- No Kubernetes, cloud deployment, or distributed infrastructure is
  introduced.
