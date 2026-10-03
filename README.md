# OpsFlow

OpsFlow is an enterprise work-management platform with an integrated
**retrieval-augmented generation (RAG)** pipeline, built as a
production-oriented **portfolio project**. It demonstrates the end-to-end
design and implementation of a secure, multi-tenant API, a professional
React frontend, document ingestion and chunking, hybrid vector + lexical
retrieval, grounded answer generation with citations, and automated testing
at every layer.

## Project status

OpsFlow is under **active development**. The backend API, authentication
system, document pipeline, and full RAG answer pipeline are implemented and
tested. The frontend provides authentication, project management, and document
upload workflows.

**What is working now:**

- Multi-tenant authentication with JWT access tokens and refresh-token rotation
- Organization-scoped project and document management
- Document upload with automatic ingestion: uploading a document triggers text
  extraction, chunking, and embedding generation synchronously, so the document
  is indexed and searchable as soon as the upload response returns
- Embedding generation (OpenAI text-embedding-3-small)
- Hybrid retrieval: semantic vector search + SQL Server Full-Text lexical search,
  fused via Reciprocal Rank Fusion
- Optional reranking via Amazon Bedrock (Cohere Rerank 3.5), config-gated
  (default OFF)
- Grounded answer generation with structured citations and validation
- RAG pipeline observability via BCL `System.Diagnostics.Metrics`
- RAG retrieval evaluation framework (MRR@K, NDCG@K, Recall@K)
- React frontend with login, token refresh, project creation, document workspace,
  grounded RAG question-answering with citations, and hybrid document search
- CI pipeline with backend and frontend gates on every push and PR

**Major pieces not yet implemented:**

- Telemetry export (metrics instruments exist but no exporter is configured)
- Production cloud deployment infrastructure

## Architecture

### Backend — .NET 10 modular monolith

The backend follows Clean Architecture with strict dependency inversion.
Technology choices are isolated behind application-layer port interfaces, so
providers (OpenAI, Bedrock, EF Core) can be swapped without touching business
logic.

| Layer | Project | Role |
|---|---|---|
| Domain | `OpsFlow.Domain` | Entities with validated invariants, zero dependencies |
| Application | `OpsFlow.Application` | Port interfaces, services, commands/queries, DTOs |
| Contracts | `OpsFlow.Contracts` | API request/response records shared with the frontend |
| Infrastructure | `OpsFlow.Infrastructure` | EF Core, ASP.NET Identity, OpenAI, Bedrock, observability |
| API | `OpsFlow.Api` | ASP.NET Core controllers, JWT authentication, composition root |

### Frontend — React 19 SPA

React 19 + TypeScript 6 + Vite 8 single-page application. Handles
authentication flows (login, silent refresh, logout), project management,
and document upload/listing. Connected to the backend API via an HTTP client
with automatic token refresh.

### Data — SQL Server 2025

Microsoft SQL Server 2025 (Developer edition) running in Docker for local
development. Provides native vector storage (`vector` type) for semantic
search, Full-Text Search indexes for lexical retrieval, and application locks
for authentication concurrency control.

## Implemented capabilities

### Core platform

- **Multi-tenancy** — every user belongs to an organization; all queries are
  scoped to the user's organization
- **Role-based authorization** — four roles (Organization Administrator,
  Coordinator, Technician, Viewer) are enforced at the endpoint level via
  named authorization policies; Viewer is read-only, Technician can contribute
  documents but not manage projects, unauthenticated requests receive 401 and
  insufficient roles receive 403
- **Health checks** — `GET /health/live` (liveness, no dependencies) and
  `GET /health/ready` (readiness, includes SQL Server connectivity);
  anonymous, no sensitive data exposed
- **CORS** — explicit origin allowlist via configuration; no wildcard
  production origins; credentialed requests are allowed only for trusted origins
  so the HttpOnly refresh-cookie flow works for same-site cross-origin deployments
- **Rate limiting** — four named policies (AuthStrict, ApiStandard,
  RagExpensive, Upload) protect abuse-sensitive and resource-intensive
  endpoints; exceeded requests receive `429 Too Many Requests` with
  `Retry-After`; all limits are configurable

### Authentication

- JWT access tokens (HS256, configurable lifetime)
- Refresh-token rotation with SHA-256 hashing and family-based revocation
- Reuse detection with automatic family revocation
- Timing-attack mitigation via constant-time dummy password verification
- SQL Server application locks for authentication concurrency

### Documents

- Upload with content-type validation (plain text and DOCX; 25 MiB file size limit)
- Automatic ingestion on upload: `IngestDocumentService` orchestrates text
  extraction, chunking, and embedding generation synchronously after the file
  is stored, so documents are indexed and searchable immediately
- Text extraction: plain text and DOCX (via OpenXml); only formats with a
  registered extractor are accepted for upload
- Deterministic overlapping chunking with configurable parameters
- Embedding generation: OpenAI `text-embedding-3-small` (1536 dimensions,
  batch size 60)
- All indexing steps are idempotent via `AddIfAbsentAsync`
- Local file system storage with organization/project-scoped paths

Uploaded documents are automatically indexed (extracted, chunked, embedded)
before the upload response returns. The individual extraction, chunking, and
embedding endpoints remain available for manual re-indexing or retry.

### AI / RAG pipeline

```
Question
  → Query embedding (OpenAI text-embedding-3-small)
  → Semantic vector retrieval (SQL Server cosine distance)
  → Lexical retrieval (SQL Server Full-Text Search)
  → Hybrid fusion (Reciprocal Rank Fusion)
  → [Optional] Reranking (Amazon Bedrock — Cohere Rerank 3.5)
  → Bounded evidence selection
  → Grounded answer generation (OpenAI, default gpt-4o-mini, structured JSON)
  → Answer validation (citation verification, contract checks)
  → Telemetry recording
```

**Retrieval policies:**

- `HybridOnly` (default) — semantic + lexical results fused via RRF
- `RerankWithHybridFallback` — adds Bedrock reranking; falls back to hybrid
  on reranker failure (`ChunkRerankingException` triggers fail-open)

Reranking is controlled by the `Reranking:ActivateInAnswerPath` configuration
flag, which defaults to `false`. When disabled, the reranker is never resolved
from the DI container.

**Exception taxonomy:**

| Exception | Behavior |
|---|---|
| `EmbeddingGenerationException` | Pipeline failure |
| `ChunkRerankingException` | Fail-open → hybrid fallback |
| `ChunkRerankingValidationException` | Fail-closed → pipeline failure |
| `AnswerGenerationException` | Pipeline failure |
| `GroundedAnswerValidationException` | Pipeline failure |

### Observability

BCL `System.Diagnostics.Metrics` instrumentation on the RAG answer pipeline
(meter: `OpsFlow.Rag`). Six instruments track request counts, failure counts,
durations (retrieval, generation, end-to-end), and evidence volume. Telemetry
measurements are privacy-safe by construction — the `GroundedAnswerMeasurement`
record struct contains no string or identifier fields.

No metrics exporter or dashboard is configured. The instruments are ready for
any OpenTelemetry-compatible collector when one is added.

### Evaluation

A retrieval evaluation framework supports offline quality measurement:

- Dataset loading and validation
- Retrieval evaluation at configurable K values
- Metrics: MRR@K, NDCG@K, Recall@K
- Report formatting for comparison across retrieval strategies

### Frontend

| Feature | Status |
|---|---|
| Login / logout | Implemented |
| Silent token refresh | Implemented |
| Protected routes | Implemented |
| Project creation and listing | Implemented |
| Document upload and listing | Implemented |
| Application shell with sidebar navigation | Implemented |
| RAG question-answering UI | Implemented |

### API endpoints

| Route | Method | Purpose |
|---|---|---|
| `api/v1/auth/login` | POST | Authenticate, return tokens |
| `api/v1/auth/refresh` | POST | Rotate refresh token |
| `api/v1/auth/logout` | POST | Revoke refresh-token family |
| `api/v1/auth/me` | GET | Current user profile |
| `api/v1/projects` | POST | Create project |
| `api/v1/projects` | GET | List projects |
| `api/v1/projects/{id}/documents` | GET | List documents |
| `api/v1/projects/{id}/documents` | POST | Upload document |
| `api/v1/projects/{id}/documents/{id}/content` | GET | Download document |
| `api/v1/projects/{id}/documents/{id}/extraction` | POST | Extract text |
| `api/v1/projects/{id}/documents/{id}/extraction` | GET | Get extraction |
| `api/v1/projects/{id}/search` | POST | Hybrid chunk search |
| `api/v1/projects/{id}/answer` | POST | RAG question answering |
| `/health/live` | GET | Liveness probe (anonymous) |
| `/health/ready` | GET | Readiness probe (anonymous) |

All endpoints except the three auth endpoints (`login`, `refresh`, `logout`)
and the two health endpoints require JWT Bearer authentication.

## Repository structure

```
OpsFlow/
├── .github/workflows/ci.yml              # CI pipeline (backend + frontend)
├── docs/
│   └── architecture/decisions/            # 15 ADRs (ADR-001 through ADR-015)
├── src/
│   ├── backend/
│   │   ├── OpsFlow.Api/                   # ASP.NET Core host, 5 controllers
│   │   ├── OpsFlow.Application/           # Ports, services, commands, DTOs
│   │   ├── OpsFlow.Contracts/             # API request/response records
│   │   ├── OpsFlow.Domain/                # Domain entities with invariants
│   │   └── OpsFlow.Infrastructure/        # EF Core, Identity, OpenAI, Bedrock
│   └── frontend/
│       └── opsflow-web/                   # React 19 + TypeScript 6 + Vite 8
├── tests/
│   ├── OpsFlow.Domain.UnitTests/          # Domain entity tests
│   ├── OpsFlow.Application.UnitTests/     # Application service tests
│   ├── OpsFlow.Infrastructure.UnitTests/  # Infrastructure tests
│   ├── OpsFlow.Api.IntegrationTests/      # API integration tests (Testcontainers)
│   ├── OpsFlow.Evaluation/                # Retrieval evaluation framework
│   └── OpsFlow.Evaluation.UnitTests/      # Evaluation metric tests
├── docker/
│   ├── nginx/default.conf                 # Nginx reverse proxy for frontend container
│   └── sqlserver-fts/                     # Custom SQL Server image (Full-Text)
├── docker-compose.yml                     # Full container stack (sqlserver, api, web)
├── Directory.Build.props                  # Shared C# build settings
├── Directory.Packages.props               # Central NuGet package versions
├── OpsFlow.sln
├── global.json                            # .NET SDK 10.0.300
├── start-opsflow.ps1                      # Local dev environment launcher
└── .env.example                           # Docker Compose env template
```

## Local development

### Prerequisites

- **.NET SDK** — version and roll-forward policy defined by
  [`global.json`](global.json) (currently 10.0.300)
- **Node.js 24** and **npm**
- **Docker Desktop** — for the local SQL Server container

### Environment configuration

Copy the public template to a local, git-ignored `.env` file:

```bash
cp .env.example .env
```

The `.env` file configures **only** Docker Compose (the SQL Server container).
The ASP.NET Core API reads its configuration (connection string, JWT signing
key, seed password) from
[user secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets)
or environment variables.

### 1. Start SQL Server

```bash
docker compose up -d sqlserver
```

The container publishes host port **14330** (see
[ADR-003](docs/architecture/decisions/ADR-003-sql-server-2025-vector-foundation.md))
and stores data in the named volume `opsflow-sql-data-2025`. The custom image
in `docker/sqlserver-fts/` enables Full-Text Search.

### 2. Backend

```bash
dotnet restore OpsFlow.sln
dotnet build OpsFlow.sln -c Release --no-restore
dotnet test OpsFlow.sln -c Release --no-build
```

In development mode with `Seed:Enabled` set to `true`, the API applies EF Core
migrations and seeds initial data on startup.

### 3. Frontend

```bash
cd src/frontend/opsflow-web
npm ci
npm run lint
npm test
npm run build
```

### Quick start

A PowerShell script automates the full local setup:

```powershell
.\start-opsflow.ps1
```

It verifies prerequisites, starts SQL Server, launches the backend and frontend
in separate terminals, and opens the browser.

### Containerized stack

Run the complete OpsFlow stack (SQL Server, backend API, frontend) in Docker:

```bash
cp .env.example .env   # edit .env with your values if needed
docker compose up -d --build
```

| Service | Container | Port | Health |
|---|---|---|---|
| SQL Server | `opsflow-sqlserver` | `14330` (host) → `1433` | `sqlcmd SELECT 1` |
| Backend API | `opsflow-api` | `8080` (internal) | `GET /health/ready` |
| Frontend | `opsflow-web` | `3000` (host) → `80` | `GET /` |

The frontend nginx proxy forwards `/api/*` and `/health/*` to the backend,
preserving the same-origin topology required by the `SameSite=Strict` refresh
cookie.

**Dependency graph:** `sqlserver` (healthy) → `api` (healthy) → `web`

**Persistent volumes:**

| Volume | Purpose |
|---|---|
| `opsflow-sql-data-2025` | SQL Server data files |
| `opsflow-documents` | Uploaded documents (`/app/storage`) |

**Migration strategy:** The API applies EF Core migrations on startup when
`APPLY_MIGRATIONS=true` (default in `docker-compose.yml`). This runs
`MigrateAsync()` only — no seed data is created in Production mode.

**Shutdown:**

```bash
docker compose down          # keeps volumes
docker compose down -v       # removes volumes (fresh start)
```

## Configuration

The backend uses the following configuration sections (configured via user
secrets or environment variables — never committed):

| Section | Purpose |
|---|---|
| `ConnectionStrings:OpsFlow` | SQL Server connection string |
| `Jwt` | Issuer, audience, signing key, token lifetimes |
| `Seed` | Development data seeding (enabled/disabled) |
| `DocumentStorage` | Local file storage base path |
| `OpenAI` | API key and answer model (default `gpt-4o-mini`) |
| `BedrockReranker` | AWS region, model ID, timeout |
| `Reranking:ActivateInAnswerPath` | Enable reranking in the answer pipeline (default `false`) |

## Testing

**1,323 tests** across six projects:

| Project | Tests | Scope |
|---|---|---|
| `OpsFlow.Domain.UnitTests` | 70 | Entity invariants and validation |
| `OpsFlow.Application.UnitTests` | 555 | Service orchestration, RAG paths, telemetry |
| `OpsFlow.Infrastructure.UnitTests` | 224 | Repositories, adapters, EF Core mappings |
| `OpsFlow.Api.IntegrationTests` | 408 | Full HTTP pipeline with Testcontainers SQL Server |
| `OpsFlow.Evaluation.UnitTests` | 66 | Retrieval metrics (MRR, NDCG, Recall) |

**CI results:** 1,322 passed, 1 skipped (after PR #35).

The single skipped test
(`RealBedrockRerankedEvaluationTests`) requires live AWS credentials and a SQL
Server container; it is intentionally excluded from normal CI runs.

Integration tests use
[Testcontainers](https://dotnet.testcontainers.org/) to spin up a real SQL
Server instance, ensuring tests run against the same database engine used in
development.

## CI

GitHub Actions runs on every push to `main` and every pull request targeting
`main` ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)):

**Backend job:** restore → build (Release) → test (Release)
**Frontend job:** install → lint → test → build

Both jobs must pass before a PR can merge. The workflow uses least-privilege
permissions (`contents: read`) and cancels in-progress runs when a newer
commit is pushed to the same branch.

## Architecture decisions

Major technical decisions are documented as Architecture Decision Records in
[`docs/architecture/decisions/`](docs/architecture/decisions/):

| ADR | Topic |
|---|---|
| [ADR-001](docs/architecture/decisions/ADR-001-modular-monolith.md) | Modular monolith |
| [ADR-002](docs/architecture/decisions/ADR-002-local-sql-server.md) | Local SQL Server |
| [ADR-003](docs/architecture/decisions/ADR-003-sql-server-2025-vector-foundation.md) | SQL Server 2025 vector foundation |
| [ADR-004](docs/architecture/decisions/ADR-004-embedding-profile-v1.md) | Embedding profile v1 |
| [ADR-005](docs/architecture/decisions/ADR-005-lexical-retrieval-full-text-search.md) | Lexical retrieval (Full-Text Search) |
| [ADR-006](docs/architecture/decisions/ADR-006-hybrid-retrieval-rrf.md) | Hybrid retrieval (RRF) |
| [ADR-007](docs/architecture/decisions/ADR-007-grounded-rag-answer-generation.md) | Grounded RAG answer generation |
| [ADR-008](docs/architecture/decisions/ADR-008-rag-evaluation-foundation.md) | RAG evaluation foundation |
| [ADR-009](docs/architecture/decisions/ADR-009-rag-reranking-foundation.md) | RAG reranking foundation |
| [ADR-010](docs/architecture/decisions/ADR-010-production-reranker-adapter.md) | Production reranker adapter |
| [ADR-011](docs/architecture/decisions/ADR-011-grounded-rag-reranking-activation.md) | Grounded RAG reranking activation |
| [ADR-012](docs/architecture/decisions/ADR-012-grounded-rag-observability.md) | Grounded RAG observability |
| [ADR-013](docs/architecture/decisions/ADR-013-endpoint-role-authorization.md) | Endpoint role authorization |
| [ADR-014](docs/architecture/decisions/ADR-014-api-production-hardening.md) | API production hardening |
| [ADR-015](docs/architecture/decisions/ADR-015-application-containerization.md) | Application containerization |

## Current limitations

The following are known gaps, documented here for transparency:

- **No telemetry export** — metrics instruments are in place but no
  OpenTelemetry exporter or dashboard is configured
- **No distributed tracing**
- **No fine-grained permissions** — authorization uses four fixed roles with
  two coarse policies; there is no dynamic permission management UI or
  per-resource access control
- **No background document processing** — text extraction, chunking, and
  embedding generation are synchronous per-request operations
- **No pagination** on list endpoints
- **No distributed rate limiting** — rate limits are per-process; multi-instance
  deployments would need Redis-backed distributed limiting
- **No proxy-aware IP detection** — rate-limit IP partitioning uses
  `RemoteIpAddress` directly; behind a reverse proxy, forwarded-header
  middleware must be configured separately
- **Cross-site refresh remains unsupported** — CORS allows credentials for trusted
  origins, but the refresh cookie remains `SameSite=Strict`; deployments on a
  different site require a separate CSRF-safe cookie/session design

## What this project demonstrates

- **Backend architecture** — Clean Architecture with strict layer separation,
  dependency inversion via port interfaces, and centralized package management
- **Security** — JWT authentication with refresh-token rotation, family-based
  revocation, reuse detection, and timing-attack mitigation; endpoint-level
  role-based authorization enforced via named policies
- **Multi-tenancy** — organization-scoped data isolation enforced at the
  query level
- **RAG pipeline engineering** — hybrid retrieval (vector + lexical + RRF),
  optional reranking with fail-open/fail-closed semantics, grounded answer
  generation with citation validation
- **Testing discipline** — 1,323 tests across unit, integration, and
  evaluation layers; Testcontainers for database-realistic integration tests
- **Continuous integration** — automated build, lint, and test gates on every
  change
- **Observability foundations** — privacy-safe metrics instrumentation ready
  for any OpenTelemetry-compatible backend
- **Containerization** — multi-stage Docker images for backend and frontend
  with nginx reverse proxy, health checks, and dependency-ordered startup
- **Decision documentation** — 15 ADRs recording the rationale behind every
  major technical choice

## License

Not yet specified.
