# ADR-017: Analytics dashboard and metrics persistence

## Status

Accepted

## Date

2026-10-15

## Context

OpsFlow needs an internal analytics dashboard that displays real operational
data for the portfolio demo. The existing RAG pipeline telemetry
(`MeterGroundedAnswerTelemetry`, ADR-012) uses `System.Diagnostics.Metrics`
which is purely in-memory — the data is lost on process restart and cannot
be queried by an API endpoint.

Key requirements:
- Persist RAG pipeline metrics to the database for historical analysis
- Expose an analytics API with tenant isolation (OrganizationId scoping)
- Build a frontend dashboard showing project stats, RAG metrics, and trends
- Separate the read-only Viewer demo account from privileged accounts
- No additional dependencies (use existing SQL Server and CSS)

## Decision

### Metrics persistence — decorator pattern

A new `PersistingGroundedAnswerTelemetry` decorator wraps the existing
singleton `MeterGroundedAnswerTelemetry`. On each `Record()` call it:

1. Delegates to the inner meter-based telemetry (BCL metrics unchanged)
2. Writes a `RagMetricEntry` row to SQL Server via `OpsFlowDbContext`

The decorator is registered as **scoped** (it needs `OpsFlowDbContext`),
while the inner `MeterGroundedAnswerTelemetry` stays **singleton** (it
owns the BCL `Meter`). `AnswerProjectQuestionService` is already scoped,
so it receives the scoped composite naturally.

OrganizationId is read from `ICallerContext`, a lightweight Application
layer port implemented by `HttpCallerContext` in the API project. This
avoids adding ASP.NET Core HTTP dependencies to the Infrastructure layer.

Persistence failures are caught and logged — they never affect the answer
pipeline (consistent with ADR-012's safety contract).

### RagMetricEntry entity

A new `RagMetricEntry` table stores one row per grounded-answer request:

| Column | Type | Purpose |
|---|---|---|
| Id | bigint (identity) | Primary key |
| OrganizationId | uniqueidentifier | Tenant isolation |
| RecordedAt | datetimeoffset | When the request occurred |
| Outcome | nvarchar(50) | Bounded enum name (Answered, Failed, etc.) |
| RetrievalMode | nvarchar(50) | Hybrid, Reranked, etc. |
| FailureCategory | nvarchar(50) | Nullable; set only for Failed outcomes |
| SelectedEvidenceCount | int | Nullable; chunks selected for generation |
| TotalDurationSeconds | float | End-to-end request duration |
| RetrievalDurationSeconds | float | Retrieval phase duration |
| GenerationDurationSeconds | float | Nullable; generation phase duration |

Indexed on `(OrganizationId, RecordedAt)` for dashboard queries. Stores
enum names as strings (not integers) for readability and forward
compatibility. Carries no user content, consistent with ADR-012.

### Analytics API

A single `GET /api/v1/analytics/summary` endpoint returns project counts,
document counts, and aggregated RAG metrics scoped to the caller's
organization. Access requires the `AnalyticsRead` policy (Organization
Administrator and Coordinator); Technician and Viewer receive 403. The
organization is taken only from the caller's JWT claim. Outcome totals and
averages are all-time; the daily trend covers the last 30 UTC calendar days
(today and the previous 29), filtered before grouping, so sparse historical
activity never appears in the chart.

The frontend mirrors the policy for navigation only: Administrators and
Coordinators land on the analytics dashboard, while Technicians and Viewers
land on a role-appropriate welcome page and never call the analytics
endpoint. The API remains the authority.

### Demo account security

`SeedOptions` gains a `ViewerPassword` property. The `DevelopmentDataSeeder`
uses `DefaultPassword` for privileged accounts (admin, coordinator,
technician) and `ViewerPassword` for the read-only Viewer. Null, empty and
whitespace values all count as "not configured" (Docker Compose passes an
unset variable through as an empty string).

- **Development seeding** (Development + `Seed:Enabled`, or Testing): an
  unset `ViewerPassword` falls back to `DefaultPassword` for convenience.
- **Public demo seeding** (`SEED_DEMO_DATA=true` in Production): both
  passwords are required. Seeding fails before writing anything if either is
  missing.
- In every mode, a `ViewerPassword` equal to `DefaultPassword` is rejected.
  Error messages name the settings and never include password values.
- Configured passwords are authoritative for the seeded accounts. An existing
  account whose password differs is updated in a single write, after the
  Identity password validators pass. That write also rotates the security
  stamp, revoking access and refresh tokens issued under the old password.
- Databases seeded before this change gave the Viewer `DefaultPassword`, so
  that password must be treated as public. Public demo seeding refuses to run
  while the existing Viewer still accepts `DefaultPassword`, and instructs the
  operator to choose a new one. Once rotated, the old shared password no
  longer authenticates any seeded account.

This ensures the public demo can share the Viewer credential without
exposing privileged accounts.

### Frontend dashboard

The existing placeholder `DashboardPage` is replaced with an analytics
dashboard using CSS-only visualizations (no chart library). It shows:

- Summary cards: project count, document count, RAG queries, success rate
- Performance metrics: average durations and evidence counts
- Outcome breakdown: horizontal bar chart
- Daily trend: vertical bar chart (last 30 days)

The design uses the existing CSS custom property system (`--bg`, `--surface`,
`--border`, `--text`, `--text-muted`) and is responsive.

## Consequences

- RAG metrics survive process restarts and are queryable via the API
- The decorator pattern preserves the existing `IGroundedAnswerTelemetry`
  contract — no changes to `AnswerProjectQuestionService`
- `ICallerContext` provides a clean boundary between HTTP concerns and
  infrastructure
- The analytics endpoint adds one round-trip per dashboard load; acceptable
  for a portfolio demo
- Demo seeding includes 30 days of realistic metrics data (deterministic
  seed, `Random(42)`) so the dashboard is populated on first visit
- The Viewer credential can be published safely in demo documentation
- No new npm dependencies — dashboard uses CSS-only charts
- NuGet audit warnings (`NU1902`, `NU1903`) for the transitive
  `System.Security.Cryptography.Pkcs` dependency are suppressed in
  `Directory.Build.props` — a pre-existing issue unrelated to this work
