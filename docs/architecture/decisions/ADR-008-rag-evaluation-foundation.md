# ADR-008: RAG retrieval evaluation foundation

- **Status:** Accepted
- **Date:** 2026-09-11
- **Context from:** [ADR-006](ADR-006-hybrid-retrieval-rrf.md),
  [ADR-007](ADR-007-grounded-rag-answer-generation.md)

## Context

Hybrid retrieval (ADR-006) and grounded answer generation (ADR-007) ship with
several **unevaluated defaults**: the RRF constant `K = 60`, the candidate depth
of 50, equal weighting of the semantic and lexical signals, and — in the RAG
layer — `EvidenceTopK = 8` and `MaxEvidenceChars = 12_000`. Both ADRs explicitly
record that tuning these values "requires evaluation infrastructure not yet
built."

Before introducing reranking (a future PR), we must be able to **measure**
retrieval ranking quality and **detect regressions**, so that any future change
can be shown to improve on a known baseline rather than asserted to. This ADR
establishes that measurement foundation.

The overriding constraint is honesty. A small synthetic benchmark can validate
ranking plumbing and catch regressions; it cannot prove real-world accuracy. The
design and all reporting must state this plainly.

## Decision

### Evaluation is distinct from correctness testing

Ordinary unit/integration tests answer "does the code behave to contract?" This
foundation answers a different question — "how good is the ranking?" The two are
kept separate: retrieval-quality evaluation lives in its own library and cases,
never disguised as ordinary tests.

### System under evaluation

The evaluated system is the **retrieval ranking** produced by
`SearchDocumentChunksHybridService` → real semantic vector retrieval
(`EfSemanticChunkRetriever`, SQL `VECTOR_DISTANCE`) + real lexical retrieval
(`EfLexicalChunkRetriever`, SQL `FREETEXTTABLE`) + real `ReciprocalRankFusion`.
The HTTP controller, the answer generator, citations, and the frontend are **out
of scope**. Answer-generation quality is explicitly not evaluated here.

### Project placement and isolation

Evaluation code is isolated from production. A support library
`tests/OpsFlow.Evaluation` holds the dataset contract, validation, metric
primitives, the neutral hit type, the evaluator, and the report formatter, with
the versioned dataset embedded as a resource. It references **no** production
project, and no production project references it. Its unit tests live in
`tests/OpsFlow.Evaluation.UnitTests`; the SQL-backed baseline reuses the existing
`OpsFlow.Api.IntegrationTests` infrastructure (the `SqlServerFixture`
Testcontainers fixture) rather than duplicating it.

### Versioned synthetic dataset with symbolic identifiers

The dataset (`opsflow-retrieval-synthetic-v1`, JSON) is a synthetic operations
corpus (deployment, change management, incident response, access control, backup
and recovery, security, service ownership) with deliberate distractors — chunks
that share keywords but do not answer a given query. Gold labels reference
chunks by **stable symbolic keys**, never database GUIDs, so the dataset is
human-reviewable and decoupled from any run. The SQL baseline builds a
`chunkKey ↔ runtime GUID` map at seed time. All content is fabricated; it
contains no customer data, personal data, or secrets.

### Graded relevance

Relevance is graded `0..3`: `0` irrelevant (expressed by omission), `1` partially
relevant, `2` relevant, `3` highly relevant. Validation rejects negative grades,
grade 0 written explicitly, grades above 3, unknown chunk keys, duplicate chunk
keys (within or across documents), duplicate document keys, duplicate case ids,
blank fields, and cases with no relevant chunk. The dataset is never silently
repaired. Gold grades are authored from the **meaning** of each query, never from
observed rankings — labelling to match a ranking would make the benchmark
circular.

### Metrics

Three metrics are implemented, all over graded relevance where "relevant" for the
binary metrics means grade ≥ 1:

- **Recall@K** = (gold-relevant chunks appearing within the first K) / (total
  gold-relevant chunks for the case). The denominator is the gold count, never K.
- **MRR@K** = mean over cases of the reciprocal rank of the first relevant result
  within K, or 0 when none appears within K.
- **nDCG@K** with exponential gain `2^rel - 1` and discount `log2(rank + 1)` over
  1-based ranks; `IDCG@K` is the DCG of the ideal ordering (gold grades sorted
  descending, truncated to K); `nDCG = DCG / IDCG`.

**Precision@K is intentionally omitted.** On a small synthetic corpus with few
relevant chunks per case, Precision@K is dominated by `relevant/K` when K exceeds
the relevant count and adds noise rather than signal. Recall, MRR, and nDCG cover
"did we retrieve the relevant chunks" and "are they ranked well." Metrics are
computed at **K ∈ {1, 3, 5, 8}**; K = 8 is the headline because it equals the
RAG layer's `EvidenceTopK`. K = 50 (the internal candidate depth) is not reported
because no consumer sees it.

### Result validation

Ranked results are validated, never silently repaired: ranks must be contiguous
and 1-based in list order, chunk keys must be unique, and every key must exist in
the dataset corpus. A key that exists in the corpus but is absent from a case's
relevance map is valid and scores grade 0. Malformed rankings (duplicate keys,
rank gaps, out-of-order ranks, unknown keys) are rejected so a retrieval bug
cannot be masked.

### Deterministic execution and the fake-provider boundary

Evaluation runs at two levels: pure metric unit tests (no database) and a
SQL-backed baseline that exercises the real retrieval pipeline. The baseline
replaces **only** `IEmbeddingGenerator` with a deterministic content embedding.
That embedding tokenizes text (invariant lowercasing, alphanumeric runs), hashes
each token with **SHA-256** into a stable bucket in `[0, 1536)`, accumulates term
frequencies, and L2-normalizes. It uses no `GetHashCode`, `Random`, GUIDs, or
culture-dependent operations, so it is byte-for-byte reproducible across
processes and machines. The **same** function produces both the seeded corpus
vectors and the query vector, so the semantic channel is meaningful.

### No external model or network dependency in CI

Evaluation never calls OpenAI, Anthropic, Cohere, Voyage, any reranker, any
LLM-as-a-judge, or the network. It is fully reproducible and free. Any future
live/provider benchmark is separate tooling.

### Tenant isolation is a hard invariant

The SQL baseline seeds a single organization/project and maps every returned
chunk id back to that organization's corpus; a foreign id would fail the run. A
dedicated test additionally seeds a second organization holding a chunk whose
text nearly duplicates a high-value chunk of the first, and asserts it never
appears in the first organization's rankings.

### Baseline / regression policy

For this PR the baseline asserts **architectural invariants only**: every result
is a tenant-scoped corpus chunk, there are no duplicate results, result counts do
not exceed the requested cut-off, every case is scored, and aggregate metrics lie
in `[0, 1]`. It does **not** assert metric thresholds. No `nDCG ≥ x` gate is
committed without evidence — the first green SQL-backed CI run establishes the
observable baseline, and code is not tuned after the fact merely to match
observed scores. Future PRs compare candidate pipelines against that established
measurement system.

### Neutral hit contract for future reranker comparison

The evaluator consumes a neutral `RetrievalEvaluationHit(ChunkKey, Rank)` and has
no dependency on `HybridChunkHit`, RRF, SQL, or provider types. A future PR that
adds `hybrid → reranker` can produce the same neutral hits and be scored by the
identical dataset, metrics, and evaluator, so baseline (RRF-only) and candidate
(reranked) results are directly comparable.

## Consequences

- **A measurable retrieval baseline exists**: metric primitives with exact unit
  tests, a validated synthetic dataset, and a deterministic SQL-backed baseline
  that exercises the real retrieval path.
- **Reranking can be evaluated later** against the same cases and metrics without
  redesigning the framework.
- **Limitations, stated honestly**:
  - The deterministic embedding validates retrieval/ranking plumbing and
    regression behavior; it does **not** measure real embedding semantic quality.
  - Because the surrogate embedding is token-derived, the semantic channel
    correlates with the lexical channel, so this baseline under-represents the
    independent contribution of semantic retrieval in production.
  - The corpus is small and synthetic. This is a **deterministic regression
    baseline / offline synthetic benchmark**, **not** a real-world RAG accuracy
    measurement. Real-domain evaluation would require larger representative
    datasets, human relevance judgments, and possibly live provider benchmarking.
- **CI is the SQL-backed execution gate.** The pure metric and validation tests
  run everywhere; the SQL baseline requires the Testcontainers SQL Server and
  therefore runs in CI (and locally only where Docker is available).

## Future work

- **Reranker comparison** (next): score `hybrid RRF` vs `hybrid RRF → reranker`
  on this dataset and these metrics.
- **Answer-generation evaluation** (later, separate metric family, not built
  here): citation correctness, citation completeness, groundedness, answer
  relevance, and insufficient-evidence accuracy. LLM-as-a-judge is explicitly not
  introduced now and must not be conflated with retrieval metrics.
- **Larger, human-judged datasets** and optional live/provider benchmarks as
  separate tooling.

## Alternatives considered

- **Pure in-memory ranking fixtures only**: would not exercise the real semantic
  vector and full-text retrieval paths, so it could not catch regressions in the
  actual pipeline. Rejected in favor of the combined approach.
- **Real embedding provider in CI**: non-deterministic, costly, and network-
  dependent. Rejected; the deterministic surrogate keeps CI reproducible and
  free, with the limitation documented.
- **Binary relevance**: cannot distinguish "highly" from "partially" relevant and
  weakens nDCG. Rejected in favor of graded `0..3`.
- **Exact golden-ranking assertions for the SQL baseline**: brittle when equally
  relevant results swap order under full-text rank ties. Rejected in favor of
  invariants now and evidence-based metric gates later.
- **Committing arbitrary metric thresholds up front**: would encode intuition as
  fact. Rejected; the first green CI run establishes the baseline.
- **Coupling the evaluator to `HybridChunkHit`/RRF**: would prevent reusing the
  harness for a reranked pipeline. Rejected in favor of the neutral hit type.
- **A command-line evaluation runner in this PR**: deferred. A CLI over the live
  pipeline needs Docker/SQL and is not CI-deterministic, and a CLI over synthetic
  rankings adds little beyond the report formatter. It can be added later as a
  thin wrapper over the same evaluator.
