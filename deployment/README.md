# OpsFlow Production Deployment

This directory contains the production deployment configuration for OpsFlow.

## Architecture

```
Internet → Caddy :443 (TLS) → API :8080     (/api/*, /health/*)
                              → nginx :80    (frontend static files)
           SQL Server Express :1433          (internal only, database network)
```

- **Caddy** (deterministic IP `172.30.10.10`) handles TLS termination
  (automatic Let's Encrypt) and proxies API traffic directly to the .NET
  backend — single proxy hop, no Caddy→nginx→API double hop
- **nginx** serves the React SPA static files with SPA fallback
- **SQL Server 2025 Express** is free for production use

### Network topology

| Network | Subnet | Services | Internet access |
|---|---|---|---|
| `opsflow-public` | `172.30.10.0/24` | Caddy (`172.30.10.10`), API, nginx | Yes (via Caddy) |
| `opsflow-database` | Docker-assigned | API, SQL Server | No (internal) |

Only Caddy publishes ports (80, 443). The API trusts forwarded headers only
from `172.30.10.10` (Caddy's pinned IP) with `ForwardLimit=1`.

### Image scheme

Production deployments use GHCR images pinned to exact commit-SHA tags:

```
ghcr.io/<owner>/opsflow-api:sha-<8chars>
ghcr.io/<owner>/opsflow-web:sha-<8chars>
```

The deploy workflow builds, pushes, and deploys the exact images for the
selected commit. There is no `latest` tag dependency for deployment
correctness.

## Prerequisites

- Linux VPS with Docker Engine 24+ and **Docker Compose >= 2.24.4**
  (required for `!override` YAML tag support)
- Domain name with DNS A record pointing to the VPS
- Port 80 and 443 open (for Caddy's ACME challenge and HTTPS)
- OpenAI API key (for embedding and RAG features)

### Verify Docker Compose version

```bash
docker compose version
# Must show >= 2.24.4
```

If the version is too old, update Docker Compose:

```bash
# Using Docker's official repository (Debian/Ubuntu):
sudo apt-get update && sudo apt-get install docker-compose-plugin
```

## Initial setup

### 1. Clone the repository

```bash
git clone https://github.com/cristhiano1/OpsFlow.git /opt/opsflow
cd /opt/opsflow
```

### 2. Create the environment file

```bash
cp deployment/.env.production.example .env
```

Edit `.env` and replace every `CHANGE_ME` value:

| Variable | How to generate |
|---|---|
| `OPSFLOW_DOMAIN` | Your domain (e.g., `demo.opsflow.dev`) |
| `MSSQL_SA_PASSWORD` | `openssl rand -base64 24` |
| `JWT_SIGNING_KEY` | `openssl rand -base64 32` |
| `SEED_DEFAULT_PASSWORD` | Choose a password for demo accounts |
| `OpenAI__ApiKey` | From [platform.openai.com](https://platform.openai.com) |
| `OPSFLOW_API_IMAGE` | GHCR image ref (set by deploy workflow, or manually) |
| `OPSFLOW_WEB_IMAGE` | GHCR image ref (set by deploy workflow, or manually) |

### 3. Start the stack

For first-time setup (building images locally):

```bash
# Set image refs for local build
export OPSFLOW_API_IMAGE=opsflow-api:local
export OPSFLOW_WEB_IMAGE=opsflow-web:local
docker compose -f docker-compose.yml -f docker-compose.production.yml up -d --build
```

For deployments using pre-built GHCR images (the normal path via the deploy
workflow):

```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml pull api web
docker compose -f docker-compose.yml -f docker-compose.production.yml up -d --no-build
```

Caddy will automatically obtain a TLS certificate from Let's Encrypt on the
first request to the configured domain.

### 4. Verify

```bash
# Check all containers are healthy
docker compose -f docker-compose.yml -f docker-compose.production.yml ps

# Check API health
curl -sf https://YOUR_DOMAIN/health/ready

# Check that no ports are leaked
docker compose -f docker-compose.yml -f docker-compose.production.yml config | grep -A2 ports:
```

## Demo accounts

`SEED_DEMO_DATA` defaults to `false`. For the intentional initial demo seed,
set it to `true`, start the stack once, verify the accounts/data, then set it
back to `false` for subsequent deployments.

When `SEED_DEMO_DATA=true`, the following accounts are created idempotently:

| Email | Role | Organization |
|---|---|---|
| `viewer@opsflow.local` | Viewer | Northwind Field Services |
| `coordinator@opsflow.local` | Coordinator | Northwind Field Services |
| `technician@opsflow.local` | Technician | Northwind Field Services |
| `admin@opsflow.local` | Organization Administrator | Northwind Field Services |
| `admin@contoso.local` | Organization Administrator | Contoso Operations |

All accounts use the password set in `SEED_DEFAULT_PASSWORD`.

## Updates

### Via GitHub Actions

Trigger the Deploy workflow from the Actions tab. It builds images tagged with
the exact commit SHA, pushes to GHCR, and deploys the pinned images via SSH.
No `latest` tag is used for deployment correctness.

### Manual update

```bash
cd /opt/opsflow
git pull
docker compose -f docker-compose.yml -f docker-compose.production.yml up -d --build
```

**Important:** Always use `docker compose up -d`, never `docker compose restart`.
The `restart` command does not respect `depends_on` health checks, which can
cause the API to crash with "Database already exists" (Error 1801) if it starts
before SQL Server is ready.

### Restart behavior

All long-running services (sqlserver, api, web, caddy) use
`restart: unless-stopped`. This means:

- Containers restart automatically after a crash or Docker daemon restart
- Containers stay stopped only if you explicitly stop them with
  `docker compose stop` or `docker compose down`
- After a host reboot, Docker starts the daemon, which restarts the containers

## Backup and restore

### Database backup

```bash
docker exec opsflow-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C \
  -Q "BACKUP DATABASE [OpsFlow] TO DISK = '/var/opt/mssql/backup/opsflow.bak' WITH INIT"
```

Copy the backup file from the container:

```bash
docker cp opsflow-sqlserver:/var/opt/mssql/backup/opsflow.bak ./opsflow-$(date +%Y%m%d).bak
```

### Database restore

```bash
docker cp opsflow.bak opsflow-sqlserver:/var/opt/mssql/backup/opsflow.bak

docker exec opsflow-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C \
  -Q "RESTORE DATABASE [OpsFlow] FROM DISK = '/var/opt/mssql/backup/opsflow.bak' WITH REPLACE"
```

### Document storage backup

Uploaded documents are stored in the `opsflow-documents` Docker volume:

```bash
docker run --rm -v opsflow-documents:/data -v $(pwd):/backup alpine \
  tar czf /backup/opsflow-documents-$(date +%Y%m%d).tar.gz -C /data .
```

## Shutdown

```bash
# Stop containers (keeps volumes/data)
docker compose -f docker-compose.yml -f docker-compose.production.yml down

# Stop and remove all data (destructive)
docker compose -f docker-compose.yml -f docker-compose.production.yml down -v
```

## Troubleshooting

### API won't start

Check logs:

```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml logs api
```

Common causes:
- SQL Server not yet healthy (wait for healthcheck, use `up -d` not `restart`)
- Invalid JWT signing key (must be valid Base64, minimum 32 bytes)
- Missing required environment variables

### Caddy certificate errors

Ensure:
- DNS A record points to the VPS IP
- Ports 80 and 443 are open
- The domain in `OPSFLOW_DOMAIN` matches the DNS record

Check Caddy logs:

```bash
docker compose -f docker-compose.yml -f docker-compose.production.yml logs caddy
```

### RAG features unavailable

Document upload will succeed (file stored, text extracted, chunked) but
embedding generation will fail without a valid `OpenAI__ApiKey`. This is
expected — the platform degrades gracefully to document storage without AI
features.
