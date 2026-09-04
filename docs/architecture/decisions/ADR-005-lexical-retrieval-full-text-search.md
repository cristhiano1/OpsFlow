# ADR-005: Lexical retrieval via SQL Server Full-Text Search

- **Status:** Accepted
- **Date:** 2026-09-02
- **Context from:** [ADR-003](ADR-003-sql-server-2025-vector-foundation.md),
  [ADR-004](ADR-004-embedding-profile-v1.md)

## Context

OpsFlow needs a lexical retrieval signal to complement the existing semantic
(vector-based) retrieval. The two signals will be fused via Reciprocal Rank
Fusion (RRF) in a future PR to produce hybrid search results. This ADR covers
only the lexical retrieval foundation.

## Decision

### Engine

Use SQL Server 2025 built-in Full-Text Search (FTS). FTS is a native SQL
Server capability that provides tokenization, stemming, ranking, and
full-text indexing without an external search service.

### Docker image

The standard `mcr.microsoft.com/mssql/server:2025-latest` image does **not**
include FTS — `SERVERPROPERTY('IsFullTextInstalled')` returns `0`. The
`mssql-server-fts` package is available from the SQL Server 2025-specific
Microsoft repository (`packages.microsoft.com/ubuntu/24.04/mssql-server-2025`),
not the generic prod repository that ships with the base image.

A custom Dockerfile (`docker/sqlserver-fts/Dockerfile`) derives from the base
image, adds the SQL Server 2025 repository, installs `mssql-server-fts`, and
produces a deterministic FTS-enabled image used by both local development
(`docker-compose.yml`) and integration tests (Testcontainers via
`ImageFromDockerfileBuilder`).

### Ranking function

FREETEXTTABLE is used instead of CONTAINSTABLE:

- FREETEXTTABLE accepts plain natural-language text. User input is never
  parsed as FTS operators, eliminating FTS operator injection risk without
  requiring a custom parser or escaping logic.
- CONTAINSTABLE requires structured FTS predicates (`AND`, `OR`, `NEAR`,
  `FORMSOF`, wildcards). Safely handling user-supplied text would require
  building a mini-language parser — unnecessary complexity for a lexical
  retrieval signal.

### EF Core 10 limitation

EF Core 10.0.10 exposes only boolean FTS predicates (`EF.Functions.FreeText`
and `EF.Functions.Contains`). Ranked table-valued functions
(`FreeTextTable`/`ContainsTable`) are not available until EF Core 11. The
retriever uses `Database.SqlQuery<T>(FormattableString)` with parameterized
raw SQL.

### Language

`LANGUAGE 0` (Neutral). The Neutral word breaker and stemmer provide:

- Generic Unicode word breaking (splits on whitespace and punctuation)
- Basic inflectional expansion (not "no stemming" — simpler than
  language-specific stemmers, but does perform some inflectional generation)
- No language-specific thesaurus expansion by default

This is appropriate for a general-purpose lexical retrieval signal. A
language-specific configuration can be added later if needed.

### Global-corpus ranking

The full-text index spans all rows in `DocumentChunks` across all tenants and
projects. FREETEXTTABLE RANK uses corpus-wide IDF statistics, meaning other
tenants' documents can influence the relative relevance score of a matching
chunk. This is a **relevance quality coupling**, not a data leakage issue:

- SQL-level `WHERE` on `Documents.OrganizationId` and `Documents.ProjectId`
  prevents any cross-tenant row from appearing in results.
- Raw `FtsRank` is internal only — never exposed through any public API.
- Future RRF fusion consumes result **positions** (1-based rank order), not
  absolute RANK values, further isolating the consumer from corpus-level
  statistics.

Per-tenant or per-project full-text catalogs/indexes would require dynamic DDL
and fundamentally different migration architecture. Not justified for v1.

### Tenant/project filtering and TopK

The `top_n_by_rank` parameter of FREETEXTTABLE is **not used**. It truncates
results before the relational JOIN and WHERE clause, which means global
top-N results from other tenants could consume all slots and hide valid
target-project matches. TopK is enforced via `OFFSET 0 ROWS FETCH NEXT @topK
ROWS ONLY` after tenant/project filtering.

### Migration

FTS catalog and index creation uses `MigrationBuilder.Sql()` with
`suppressTransaction: true`. SQL Server's `CREATE FULLTEXT INDEX` cannot
execute inside a user transaction — omitting this flag causes the migration to
fail at runtime.

### Accent sensitivity

The full-text catalog inherits accent sensitivity from the database collation
(`SQL_Latin1_General_CP1_CI_AS` — case-insensitive, accent-sensitive). No
explicit `ACCENT_SENSITIVITY` override is applied. Accent-insensitive search
is a potential future enhancement.

### Population

The full-text index uses `CHANGE_TRACKING AUTO`. DML changes are indexed
asynchronously. Integration tests poll table-level properties
(`TableFulltextPopulateStatus` and `TableFulltextPendingChanges` via
`OBJECTPROPERTYEX`) with a bounded timeout to ensure population completes
before asserting on FTS results. The deprecated catalog-level
`FULLTEXTCATALOGPROPERTY` is not used.

## Consequences

- **Lexical retrieval is available** as a ranked signal for future hybrid/RRF
  fusion without an external search service.
- **Custom Docker image required** for both local development and CI/testing.
  The Dockerfile is committed and shared.
- **Raw SQL** is used for the retriever query — changes to the query shape
  require manual SQL maintenance rather than LINQ refactoring.
- **Global-corpus IDF coupling** is accepted for v1. If ranking quality
  degrades with scale, per-tenant indexing can be revisited.
- **Future RRF** consumes ordered positions from both semantic and lexical
  retrievers independently.
