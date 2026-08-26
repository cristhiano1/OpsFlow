# ADR-003: Use SQL Server 2025 for native vector capability

- **Status:** Accepted
- **Date:** 2026-08-26
- **Supersedes:** [ADR-002](ADR-002-local-sql-server.md)

## Context

OpsFlow previously used SQL Server 2022 Developer in Docker for local
development, as documented in ADR-002. That setup is sufficient for relational
workloads but does not provide the native `VECTOR` data type required for the
upcoming embedding and retrieval foundation.

SQL Server 2022 does not support the `VECTOR` type. Native vector capability —
`vector(N)` columns, `VECTOR_DISTANCE`, and the associated EF Core 10
`SqlVector<float>` / `EF.Functions.VectorDistance` APIs — requires SQL Server
2025 (major version 17).

OpsFlow already targets .NET 10 with `Microsoft.EntityFrameworkCore.SqlServer`
10.x and `Microsoft.Data.SqlClient` 6.x. Both packages expose
`Microsoft.Data.SqlTypes.SqlVector<float>` and `EF.Functions.VectorDistance`
natively, without a `ValueConverter` or external vector store, when the
underlying server is SQL Server 2025.

The goal at this stage is to establish the platform foundation only. No
production embedding schema or vector-search endpoint is introduced in this PR.

## Decision

Upgrade local development and integration tests from SQL Server 2022 to
**SQL Server 2025 Developer edition** in Docker.

Key configuration:

- **Image:** `mcr.microsoft.com/mssql/server:2025-latest`
  (`ACCEPT_EULA=Y`, `MSSQL_PID=Developer`)
- **Isolated named volume:** `opsflow-sql-data-2025` mounted at
  `/var/opt/mssql` — a new, separate volume that starts fresh and does not
  inherit the 2022 data.
- **Old volume preserved:** `opsflow-sql-data` (the SQL Server 2022 volume)
  is intentionally left on disk and is not mounted by the current Compose
  configuration. It is not deleted, pruned, or migrated.
- **Health check:** unchanged — `SQLCMDPASSWORD` + `/opt/mssql-tools18/bin/sqlcmd`
  path, which is confirmed present in the 2025 image.
- **Integration tests:** `SqlServerFixture` (Testcontainers) pulls
  `mcr.microsoft.com/mssql/server:2025-latest`, applies EF Core migrations, and
  hands out typed `OpsFlowDbContext` instances pointing at the ephemeral
  container.
- **Vector capability proof:** `VectorCapabilityTests` verifies that the 2025
  container, `SqlVector<float>`, and `EF.Functions.VectorDistance` work
  end-to-end. The proof uses a test-only table (`VectorCapabilityProbe`) that
  is created and dropped within the test class lifecycle and is not part of the
  production schema.

SQL Server 2025 is the foundation for later native vector persistence and
vector-distance search within the same relational boundary.

## Consequences

**Positive**

- Native `VECTOR` / `VECTOR_DISTANCE` platform available without an additional
  database engine or external vector store.
- One database engine serves both relational and vector workloads; tenant data
  and future vector data remain within the same transactional boundary.
- EF Core 10 native `SqlVector<float>` path can be used directly — no
  `ValueConverter`, no byte serialization.
- 2022 volume preserved; rollback to ADR-002 is possible by reverting the image
  tag and volume reference.

**Trade-offs**

- Major SQL Server version upgrade (2022 → 2025); 2025 is a newer release.
- Larger initial container pull; the 2025 image is distinct from 2022.
- Local persisted data starts in the new `opsflow-sql-data-2025` volume;
  any data previously stored in `opsflow-sql-data` is not automatically
  available.
- Future production vector schema will depend on SQL Server 2025+ availability
  in the target deployment environment.
