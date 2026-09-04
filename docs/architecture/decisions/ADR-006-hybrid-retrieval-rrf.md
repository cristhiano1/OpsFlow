# ADR-006: Hybrid retrieval via Reciprocal Rank Fusion

- **Status:** Accepted
- **Date:** 2026-09-04
- **Context from:** [ADR-003](ADR-003-sql-server-2025-vector-foundation.md),
  [ADR-004](ADR-004-embedding-profile-v1.md),
  [ADR-005](ADR-005-lexical-retrieval-full-text-search.md)

## Context

OpsFlow has two independent retrieval signals: semantic (vector cosine
distance) and lexical (SQL Server Full-Text Search FREETEXTTABLE). Each has
blind spots — semantic misses exact keyword matches, lexical misses
paraphrased or conceptual similarity. Combining both signals into a single
ranked result set improves retrieval quality for downstream consumers.

The two signals produce incompatible scores: cosine distance (double,
0 = identical, smaller is better) and FREETEXTTABLE RANK (int, higher is
better, corpus-dependent). These cannot be directly combined, normalized, or
averaged without calibration data.

## Decision

### Fusion method

Reciprocal Rank Fusion (RRF) is used. RRF operates on rank positions (the
ordinal position of each result in its source list), not raw scores. This
makes it inherently score-agnostic — it does not require normalization,
calibration, or comparable scales between sources.

For a chunk appearing at 1-based rank `r` in a source:

```
contribution = 1 / (K + r)
```

For a chunk appearing in both sources at semantic rank `rs` and lexical
rank `rl`:

```
RrfScore = 1 / (K + rs) + 1 / (K + rl)
```

### K constant

Fixed at K = 60, the standard value from the original Cormack, Clarke, and
Buettcher paper. K dampens the contribution difference between adjacent
ranks. Making K configurable is premature tuning complexity with no
justification for v1.

### Candidate depth

Both retrievers are called with a fixed candidate depth of 50 (the system's
`MaxTopK`), regardless of the user's requested `topK`. This maximizes fusion
quality — a chunk at semantic rank 6 and lexical rank 2 would be missed if
the candidate depth matched a small `topK`. The total candidate pool is at
most 100 rows, trivially small compared to the embedding generation API call.
Final `topK` truncation happens after fusion.

### Deduplication identity

`DocumentChunkId` (the chunk's primary key) identifies duplicates across
sources. When the same chunk appears in both lists, metadata fields
(`DocumentId`, `ChunkIndex`, `StartOffset`, `EndOffset`, `Text`) must be
identical; inconsistency throws `InvalidOperationException` (fail-fast for
data corruption). Duplicates within a single source list also throw.

### Deterministic tie-breaking

Final ordering: `RrfScore` DESC, then best source rank ASC
(`Min(SemanticRank, LexicalRank)`), then `DocumentChunkId` ASC (using .NET
`Guid` ordering). This contract does not depend on Dictionary insertion order.

### Sequential retrieval

Both `EfSemanticChunkRetriever` and `EfLexicalChunkRetriever` inject the same
scoped `OpsFlowDbContext`. EF Core `DbContext` does not support concurrent
operations. Retrievers are called sequentially — `Task.WhenAll` is not used.
Introducing `IDbContextFactory` solely for this optimization is not justified
for v1.

### Validation

The hybrid service performs the strict superset of semantic and lexical
validation: all semantic rules (generator identity, output vector, input
bounds) plus the lexical Rune-based punctuation-only rejection. The project
existence check and embedding generation each execute exactly once.

### Internal score semantics

`RrfScore` is a relative, internal metric — not a confidence, probability, or
percentage. `SemanticRank` and `LexicalRank` (nullable, 1-based) are retained
for internal observability. Source-specific metrics (`CosineDistance`,
`FtsRank`) are not included in the fused result.

## Consequences

- **Hybrid retrieval is available** as an internal Application-layer service
  combining both signals without score normalization.
- **No HTTP endpoint** — this PR provides internal infrastructure only.
  A public search endpoint, RAG generation, and citations are future work.
- **No reranking** — RRF produces the final ordering. A cross-encoder
  reranking model is a potential future enhancement.
- **Sequential retrieval** adds latency (two database round-trips in
  series). This is acceptable for v1; concurrent retrieval via
  `IDbContextFactory` can be revisited if profiling shows retrieval latency
  dominates.
- **Fixed K = 60** may not be optimal for all query distributions. Tuning
  requires evaluation infrastructure not yet built.
- **Candidate depth = 50** is a fixed maximum. Adaptive depth based on
  retrieval quality signals is future work.

## Alternatives considered

- **Score normalization** (min-max or z-score): requires calibration data
  and assumptions about score distributions that are fragile across corpus
  changes. RRF avoids this entirely.
- **Weighted averaging**: requires choosing weights without evaluation data.
  RRF's equal treatment of both sources is a principled default.
- **Configurable K**: adds a tuning parameter with no evaluation framework
  to guide its value. Deferred until evaluation infrastructure exists.
- **Concurrent retrieval with `IDbContextFactory`**: larger architectural
  change (factory registration, scope management) not justified by the
  latency profile of two sequential database queries.
