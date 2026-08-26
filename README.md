# OpsFlow

OpsFlow is an enterprise work-management platform, built as a
production-oriented **portfolio project**. The goal is to demonstrate how a
real business application is designed and built: a secure API, a professional
frontend, relational data modeling, testing, containerization and CI.

> **Current status — Phase 0 (foundation only).**
> No business features are implemented yet. This repository currently contains
> the solution skeleton, the frontend shell, local database infrastructure and
> continuous integration. Domain modules, authentication, authorization,
> tenant isolation and dashboards are planned for later phases.

## Architecture (current)

- **Backend** — .NET 10 modular monolith (ASP.NET Core), organized into
  `Domain`, `Application`, `Contracts`, `Infrastructure` and `Api` projects.
  At this stage the API is an empty shell (no controllers, no database access).
- **Frontend** — React + TypeScript single-page application built with Vite.
  Currently a minimal application shell.
- **Local infrastructure** — Microsoft SQL Server 2025 (Developer edition) in
  Docker, used for local development only.

The backend and frontend are not yet wired to each other or to the database;
that integration belongs to later phases.

## Repository structure

```
OpsFlow/
├── .github/workflows/ci.yml         # CI: backend + frontend jobs
├── docs/architecture/               # architecture notes, ADRs, diagrams
├── src/
│   ├── backend/
│   │   ├── OpsFlow.Api/             # ASP.NET Core host (shell only)
│   │   ├── OpsFlow.Application/     # use cases (empty)
│   │   ├── OpsFlow.Contracts/      # API request/response contracts (empty)
│   │   ├── OpsFlow.Domain/         # domain model (only an assembly marker)
│   │   └── OpsFlow.Infrastructure/ # adapters (empty)
│   └── frontend/
│       └── opsflow-web/            # React + TypeScript + Vite
├── tests/
│   └── OpsFlow.Domain.UnitTests/    # backend smoke test
├── docker-compose.yml               # local SQL Server service
├── Directory.Build.props            # shared C# build settings
├── Directory.Packages.props         # central NuGet package versions
├── OpsFlow.sln
├── global.json                      # pins the .NET SDK band
└── .env.example                     # public template for local env vars
```

## Prerequisites

- **.NET SDK** version and roll-forward policy defined by
  [`global.json`](global.json).
- **Node.js 24** and **npm**.
- **Docker Desktop** (for the local SQL Server container).

## Environment configuration

`.env.example` is a **public template** committed to the repository. It contains
development placeholders only. Copy it to `.env`, which is **local and
git-ignored** — never commit a real `.env`.

```bash
cp .env.example .env
```

## Local setup

### 1. Start SQL Server (Docker)

Docker Compose reads variables from your local `.env`:

```bash
docker compose up -d sqlserver
```

The container publishes host port **14330** (see
[ADR-003](docs/architecture/decisions/ADR-003-sql-server-2025-vector-foundation.md)) and stores
data in the named volume `opsflow-sql-data-2025`.

### 2. Backend

```bash
dotnet restore OpsFlow.sln
dotnet build OpsFlow.sln -c Release --no-restore
dotnet test OpsFlow.sln -c Release --no-build
```

### 3. Frontend

```bash
cd src/frontend/opsflow-web
npm ci
npm run lint
npm test
npm run build
```

## Verification commands (summary)

| Area     | Command |
|----------|---------|
| Backend  | `dotnet restore OpsFlow.sln` |
| Backend  | `dotnet build OpsFlow.sln -c Release --no-restore` |
| Backend  | `dotnet test OpsFlow.sln -c Release --no-build` |
| Frontend | `npm ci` (in `src/frontend/opsflow-web`) |
| Frontend | `npm run lint` |
| Frontend | `npm test` |
| Frontend | `npm run build` |

These commands are also run by the CI workflow
([`.github/workflows/ci.yml`](.github/workflows/ci.yml)). The workflow is
configured to run on pushes to `main` and pull requests targeting `main`. No
successful GitHub Actions result is claimed until a real workflow run has been
observed.

## Scope note

Cloud deployment (for example Azure) and all application features are **out of
scope for Phase 0** and are planned for later phases. The architecture is
intended to remain cloud-ready by keeping infrastructure concerns isolated as
they are implemented, but no cloud resources or future cloud adapters exist in
Phase 0.

## License

Not yet specified.
