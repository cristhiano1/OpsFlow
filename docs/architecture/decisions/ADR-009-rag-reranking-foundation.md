# ADR-009: RAG reranking foundation

- **Status:** Accepted
- **Date:** 2026-09-13
- **Context from:** [ADR-006](ADR-006-hybrid-retrieval-rrf.md),
  [ADR-007](ADR-007-grounded-rag-answer-generation.md),
  [ADR-008](ADR-008-rag-evaluation-foundation.md)

## Context

Hybrid retrieval (ADR-006) produces an RRF-fused ranking whose defaults are
unevaluated. ADR-008 built a deterministic retrieval benchmark and identified
the next step: compare `hybrid RRF` against `hybrid RRF → reranker` on the same
dataset, the same evaluator, and the same metrics. This ADR introduces the
reranking foundation that enables that comparison without disturbing the
measurable baseline.

## Decision

### Reranking happens after hybrid fusion, in a wrapper

A new Application service `SearchDocumentChunksRerankedService` composes the
unchanged `SearchDocumentChunksHybridService` with a provider-neutral
`IChunkReranker`. Reranking operates on the fused hybrid candidate pool — never
on the semantic or lexical channels in isolation. The hybrid service is not
modified, so the RRF-only baseline remains directly executable and PR #27's
`RetrievalBaselineEvaluationTests` remains valid. `AnswerProjectQuestionService`
and the HTTP endpoints are untouched.

### Provider-neutral score contract

`IChunkReranker.RerankAsync` receives a `ChunkRerankRequest` (the exact query
plus `ChunkRerankCandidate` values carrying only `DocumentChunkId`,
`OriginalHybridRank`, and `Text`) and returns one `ChunkRerankScore`
(`DocumentChunkId` + finite `RelevanceScore`) per candidate. The provider
supplies scores, not an order; the Application layer decides the order. Scores
carry no assumed scale (not necessarily 0..1) and are never normalized — only
relative ordering matters.

### Authoritative metadata boundary

The reranker may control only relevance scores. All chunk metadata
(`DocumentId`, `DocumentChunkId`, `ChunkIndex`, offsets, `Text`) in the returned
`RerankedChunkHit` is projected from the retrieved `HybridChunkHit`; a
`DocumentChunkId` returned by the provider is only used to correlate a score
back to a supplied candidate, never to author metadata. This mirrors the
citation trust model of ADR-007.

### Strict, all-candidate output validation

Reranker output is untrusted and fails closed. The service requires exactly one
finite score for every supplied candidate and rejects — via
`ChunkRerankingValidationException` — a null collection, a wrong score count, an
unknown candidate id, a duplicate id, a missing candidate, or a non-finite score
(`NaN`, `±∞`). Nothing is silently dropped, deduplicated, or invented.

### Deterministic ordering

Final ordering is: `RelevanceScore` DESC, then `OriginalHybridRank` ASC, then
`DocumentChunkId` ASC. Provider response arrival order is never used. Because the
original hybrid rank is unique per candidate, the `DocumentChunkId` key is a
total-order guarantee that is not reachable through the real pipeline; it is
retained for defensiveness and determinism.

### TopK and candidate depth

The public `SearchDocumentChunksRerankedQuery.TopK` keeps the hybrid service's
1..50 semantics. The reranker candidate depth is internal:
`effective = min(50, max(20, TopK))`. Thus a small `TopK` still reranks 20
candidates, while a `TopK` above 20 reranks that many (capped at 50), and no
`TopK` in 1..50 is rejected. The hybrid service's own `CandidateDepth = 50` is
unchanged; the wrapper simply requests `TopK = effective candidate depth` from
it. **The default candidate depth of 20 is an unevaluated default and must be
measured**, exactly like the hybrid and evidence defaults before it.

### Failure semantics

The foundation is fail-closed: a reranking failure fails the call rather than
silently returning the hybrid ordering. `ChunkRerankingException` covers
provider/transport availability failures and provider-capacity mismatches;
`ChunkRerankingValidationException` covers contract violations in a well-formed
response. Caller-requested cancellation always propagates unwrapped — an
`OperationCanceledException` observed while the caller's token is cancelled is
never a provider failure — but a provider-local timeout that surfaces as
cancellation while the caller's token is not cancelled is treated as a reranker
failure and wrapped in `ChunkRerankingException`. A fail-open policy (fall back to hybrid RRF when the
reranker is unavailable), if adopted, belongs to the later RAG-activation
decision and must expose whether reranking was applied without leaking provider
internals.

### Provider strategy — production adapter deferred

No production reranker adapter ships in this PR. A real provider (a specialized
rerank API, a local ONNX cross-encoder, or LLM-based reranking through the
existing OpenAI stack) cannot be evaluated honestly inside deterministic,
network-free, secret-free CI. This PR ships the Application foundation plus a
deterministic **token-overlap test reranker** used only in tests. `Program.cs`
registers no `IChunkReranker`; the reranked service is constructed explicitly in
tests. A later PR adds the production adapter (with its options/identity, privacy
boundary, adapter tests, and DI registration) and an evidence-based quality gate.

### Evaluation methodology and honest claims

The SQL-backed comparison retrieves one fused pool (depth 20) from the real
pipeline and evaluates two orderings of that same pool — baseline (RRF) and
candidate (the real reranked service driven by the test reranker) — on the same
dataset, tenant, query, and K = {1, 3, 5, 8}, reporting per-K deltas. It asserts
architectural invariants only (tenant isolation, no duplicate/foreign ids, pool
bounds ≤ 20, metrics in [0, 1]) and is **report-only**: it never asserts the
candidate beats the baseline. A deterministic token-overlap scorer proves
orchestration, validation, ordering, tenant safety, and comparison plumbing — it
does **not** prove real-world reranking quality.

### Privacy boundary

The test reranker makes no external calls and sends nothing anywhere. When a
remote production provider is later chosen, candidate chunk text leaves OpsFlow:
inputs must remain tenant-scoped (candidates already come from org+project-scoped
retrieval; reranking cannot broaden scope), only the minimum fields
(`DocumentChunkId` for correlation, `Text`, and the query) may be sent, the API
key follows the existing options/secrets pattern, and no provider calls run in
CI. This boundary is enforced and documented when the adapter lands.

### Tenant isolation

Candidates come only from org+project-scoped hybrid retrieval, and the reranker
cannot introduce ids outside the supplied set. An integration test seeds a second
organization with a near-identical distractor chunk and asserts it never enters
Organization A's pool, reranker input, or output.

## Consequences

- Reranked retrieval is available as an Application service, with strict
  validation and deterministic ordering, without changing the hybrid baseline,
  the grounded RAG path, or any HTTP endpoint.
- Baseline-vs-candidate comparison runs on the identical PR #27 dataset,
  evaluator, and metrics; a future reranked pipeline reuses this harness
  unchanged.
- **Limitations:** the token-overlap test reranker proves plumbing only, not
  reranking quality; the candidate depth of 20 is unevaluated; the production
  provider and its privacy boundary are deferred. This is a foundation, not a
  quality result.

## Alternatives considered

- **Modify `SearchDocumentChunksHybridService` to inject reranking** — rejected:
  destroys the independently measurable RRF baseline and couples fusion to
  reranking.
- **Rerank only inside `AnswerProjectQuestionService`** — rejected: changes
  user-facing answers before quality is measured and hides reranking from the
  search path and evaluation.
- **Order contract (provider returns the final order)** — rejected in favor of a
  score contract with Application-owned deterministic ordering, so a
  misbehaving provider cannot reorder or drop results.
- **Ship a production adapter in this PR** — deferred: it cannot be evaluated in
  deterministic CI, and bundling it would mix an unproven provider with the
  foundation.
- **Fail-open in this PR** — deferred to RAG activation, where graceful
  degradation is a conscious, observable contract rather than a silent one.
