# ADR-011: Grounded RAG reranking activation and graceful degradation

- **Status:** Accepted
- **Date:** 2026-09-16
- **Context from:** [ADR-006](ADR-006-hybrid-retrieval-rrf.md),
  [ADR-007](ADR-007-grounded-rag-answer-generation.md),
  [ADR-009](ADR-009-rag-reranking-foundation.md),
  [ADR-010](ADR-010-production-reranker-adapter.md)

## Context

ADR-009 shipped a fail-closed reranking foundation and explicitly deferred the
fail-open policy to "the RAG-activation decision", which "must expose whether
reranking was applied without leaking provider internals." ADR-010 added the
production Amazon Bedrock adapter but did not activate it. This ADR activates the
reranked retrieval path inside grounded question-answering, config-gated and
default OFF, with graceful degradation.

## Decision

### Activation point and gate

`AnswerProjectQuestionService` is the single activation point. It now depends on
both `SearchDocumentChunksHybridService` and `SearchDocumentChunksRerankedService`
plus a provider-neutral `AnswerRetrievalPolicy` (`HybridOnly` or
`RerankWithHybridFallback`). The composition root maps the non-secret
configuration flag `Reranking:ActivateInAnswerPath` (default **false**) onto that
policy. The Application layer takes no dependency on any configuration/options
framework; it receives the enum. The hybrid `/search` endpoint and the standalone
reranked service are unchanged.

### Preserved baseline and citation authority

Hybrid retrieval remains independently executable and is the default answer path.
The standalone `SearchDocumentChunksRerankedService` remains fail-closed. Evidence
selection, character bounding (`EvidenceTopK = 8`, `MaxEvidenceChars = 12 000`),
prompt construction, answer validation, and citation mapping are unchanged. Both
`HybridChunkHit` and `RerankedChunkHit` are projected into a small internal
`EvidenceChunk` carrying only the authoritative fields; the generator supplies
only integer labels and can never author `DocumentId`/chunk metadata, regardless
of retrieval path.

### Fail-open vs fail-closed boundary

- Operational reranker failure (`ChunkRerankingException` — AWS/network/transport,
  configured timeout, capacity) → **fall back to hybrid RRF** and answer.
- Untrusted/malformed reranker output (`ChunkRerankingValidationException`) →
  **fail closed** (propagate; controller returns 502). Never silently fall back on
  untrusted output.
- Reranker identity/invariant misconfiguration (`InvalidOperationException`) →
  fail closed (generic 500).
- Caller cancellation (`OperationCanceledException`) → **propagate**, never fall
  back.

The activation code catches only `ChunkRerankingException` for fallback; all other
exceptions propagate.

### Corrected exception taxonomy

To make the fail-open boundary safe, malformed provider responses in the Bedrock
adapter were reclassified from `ChunkRerankingException` to
`ChunkRerankingValidationException`: a null result collection, a result missing an
index or relevance score (`BedrockRerankInvoker`), and an out-of-range provider
result index (`BedrockChunkReranker`). `ChunkRerankingException` now denotes only
operational/transport/timeout/availability failures. This ensures untrusted output
can never be laundered into an availability fallback. The AWS request shape,
timeout logic, credentials, egress, model, region, candidate policy, and ordering
are unchanged.

### Observability

`AnswerProjectQuestionResult` carries a provider-neutral `AnswerRetrievalMode`
(`NotApplicable` / `Hybrid` / `Reranked` / `HybridFallback`). `Reranked` means the
reranker actually scored the candidates — not merely that the flag was on. The API
boundary logs this mode via structured logging; it is **not** part of the public
HTTP contract, and no provider, model, credential, query, or answer text is logged.

### Normal-CI network isolation

Activation means `/answer` can reach `IChunkReranker`. Normal CI stays AWS-free two
ways: (1) the flag defaults OFF, so the default answer path never resolves the
reranker; (2) every integration test that enables the path removes the production
`IChunkReranker` and injects a deterministic fake. Tests never rely on "Bedrock
fails, so fallback works." The production `IChunkReranker` remains
`BedrockChunkReranker`.

### Fallback duplicate retrieval — knowingly accepted

On the operational-failure path, the reranked service has already performed hybrid
retrieval (candidate depth 20) before the reranker failed; the fallback then runs
hybrid retrieval again (depth 8). This second retrieval is knowingly accepted for
this PR: fallback is an exceptional provider-outage path, not the hot path, and
reusing the pool would require changing the reranked service's contract. A future
change may expose the pool to avoid the duplicate.

## Consequences

- Reranking can be enabled in grounded answers by configuration alone, with
  graceful degradation on provider outage and fail-closed handling of untrusted
  output, while the hybrid baseline and citation-authority model are preserved.
- **Limitations / no quality claim:** no real-provider reranking quality result has
  been recorded (only the opt-in evaluation harness exists), so this PR makes **no**
  claim that Cohere Rerank 3.5 improves retrieval quality. Activation therefore
  **defaults OFF** and should be enabled deliberately — ideally after a measured
  real-provider evaluation. The operational fallback provides availability, not a
  quality guarantee.

## Alternatives considered

- **Make `SearchDocumentChunksRerankedService` itself fail-open** — rejected: it
  would corrupt the evaluation harness and violate ADR-009's fail-closed
  foundation. Fail-open belongs only to the RAG activation layer.
- **Activate by default** — rejected for now: imposes latency, cost, and an
  external dependency on every answer for unproven benefit. The same code enables
  via config once quality is measured.
- **Expose retrieval mode on the public HTTP contract** — rejected: unnecessary
  API surface; structured logging + the Application result suffice.
