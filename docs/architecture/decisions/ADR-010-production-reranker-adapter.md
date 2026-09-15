# ADR-010: Production reranker adapter (Amazon Bedrock)

- **Status:** Accepted
- **Date:** 2026-09-15
- **Context from:** [ADR-009](ADR-009-rag-reranking-foundation.md)

## Context

ADR-009 shipped a provider-neutral reranking foundation (`IChunkReranker`,
`SearchDocumentChunksRerankedService`) with strict, Application-owned validation
and deterministic ordering, and explicitly deferred the production provider
adapter, its privacy boundary, and an evidence-based quality gate. This ADR adds
the first production adapter behind that unchanged port. It does **not** activate
reranking in any user-facing path — that decision (and its fail-open/fail-closed
semantics) remains deferred to a later activation PR.

## Decision

### Provider: Amazon Bedrock Rerank, Cohere Rerank 3.5

The production reranker calls the Amazon Bedrock **Rerank** API (Bedrock Agent
Runtime data plane) via the official `AWSSDK.BedrockAgentRuntime` SDK, using the
`IAmazonBedrockAgentRuntime.RerankAsync(RerankRequest, CancellationToken)`
operation. The initial model is Cohere Rerank 3.5 (`cohere.rerank-v3-5:0`) in
`eu-central-1`. OpenAI has no genuine rerank endpoint, so the existing OpenAI
stack is not reused for reranking; direct Cohere/Voyage/Jina APIs and a local
ONNX model were rejected in favour of a managed, IAM-governed AWS service that
keeps candidate text within the AWS trust boundary.

### Architecture: neutral seam confines AWS types

- `BedrockChunkReranker` (internal, implements the public `IChunkReranker`) owns
  ordering-neutral behaviour: it sends only the query and candidate texts, in
  candidate order, and correlates each returned score back to a
  `DocumentChunkId` locally by request index. It contains no AWS SDK types.
- `IBedrockRerankInvoker` is a narrow internal seam over the single rerank
  operation, taking a query and document texts and returning `(index, score)`
  pairs. No AWS SDK type crosses it.
- `BedrockRerankInvoker` is the only production file that references AWS Bedrock
  request/response and exception types. It builds the request, calls Bedrock,
  maps results, and translates every AWS/transport failure.

The Application layer (`SearchDocumentChunksRerankedService`) remains
authoritative for full result validation (exact count, duplicate/unknown/missing
ids, finiteness), deterministic final ordering, authoritative metadata, and
`TopK`. The adapter never sorts, truncates, deduplicates, fills, repairs,
normalizes, or clamps.

### Request/response mapping

`RerankRequest` carries one TEXT query, one INLINE/TEXT source per candidate (in
candidate order), and a Bedrock reranking configuration whose `ModelArn` is built
from the configured region and model id and whose `NumberOfResults` equals the
candidate count (so every candidate is scored and the Application's exact-count
validation can hold). Each `RerankResult.Index` is correlated locally to
`candidates[Index].DocumentChunkId`; an out-of-range index fails closed with
`ChunkRerankingException`, and a result missing an index or score is likewise
rejected — never invented.

### Model ARN and region

Configuration stores **Region + ModelId**, and the ARN
`arn:aws:bedrock:{Region}::foundation-model/{ModelId}` is constructed from them
(for the defaults:
`arn:aws:bedrock:eu-central-1::foundation-model/cohere.rerank-v3-5:0`). A single
region value configures both the SDK client endpoint and the ARN, so the two
cannot drift. `RerankerIdentity.ModelId` truthfully reflects the configured
model.

### Reranker identity

The adapter exposes a stable, truthful `RerankerIdentity`:

- **ProfileId:** `opsflow-rerank-v1` (a product-level constant, not configurable).
- **ModelId:** the configured Bedrock model — `cohere.rerank-v3-5:0` by default,
  configuration-driven via `BedrockReranker:ModelId`.
- **MaxCandidates:** `100` (see below).

### MaxCandidates = 100 is an OpsFlow policy boundary

`RerankerIdentity.MaxCandidates` is 100. This is **OpsFlow's operational
reranking capacity** — the maximum candidates the adapter will submit in one
request — chosen so a single rerank stays within one Bedrock/Cohere search-unit
under ordinary document sizing, and comfortably above the retrieval pool's
ceiling of 50. It is **not** a claimed Bedrock hard API limit (the service
documents a far larger sources maximum). No candidate is ever silently
truncated: if a pool exceeds `MaxCandidates`, the Application service rejects it
per its existing contract.

### Credentials and IAM

No API key and no credential fields exist in configuration. Credentials are
resolved exclusively through the AWS SDK default credential chain (environment,
shared profile, SSO, ECS task role, or IAM workload identity). Access keys,
secret keys, and session tokens are never stored in `BedrockRerankerOptions` or
`appsettings`.

Least-privilege runtime IAM for direct Rerank requires **both**:

- `bedrock:Rerank` — Resource `*` (this is the AWS pattern for direct rerank; the
  action itself is not scoped to the model ARN), and
- `bedrock:InvokeModel` — Resource
  `arn:aws:bedrock:eu-central-1::foundation-model/cohere.rerank-v3-5:0`.

Because Cohere is a third-party Bedrock model, initial model enablement may
additionally require AWS **Marketplace** subscription permissions (for example
`aws-marketplace:ViewSubscriptions`, `aws-marketplace:Subscribe`). These are
one-time account/operator **provisioning** concerns, not permanent runtime
permissions OpsFlow requires, and are not represented in application
configuration. No IAM infrastructure is implemented in this PR.

### Failure and cancellation semantics

The adapter preserves ADR-009's fail-closed contract. Caller-requested
cancellation (the caller's token is cancelled) propagates unchanged. A
provider-local timeout or cancellation observed while the caller's token is not
cancelled, and every AWS/transport failure — access denied, validation,
resource/model not found, throttling, service-quota, Bedrock service (5xx),
AWS client/network, malformed or index/score-less responses — is translated to
`ChunkRerankingException`. AWS SDK exception types never escape into the
Application layer. Failure messages are categorical and never include the query,
candidate text, request payload, or credentials.

The configured per-request timeout is enforced around the asynchronous
`RerankAsync` operation with a linked cancellation token (the AWS SDK client
timeout does not bound async calls); the original caller token remains separate
so caller cancellation and local timeout retain distinct semantics.

### Privacy / egress boundary

External inference boundary: OpsFlow → Amazon Bedrock (`eu-central-1`).

- **Sent:** the query text and the candidate chunk texts, plus the model/request
  configuration Bedrock requires.
- **Not sent:** `OrganizationId`, `ProjectId`, `DocumentId`, `DocumentChunkId`,
  `ChunkIndex`, `StartOffset`, `EndOffset`, `OriginalHybridRank`, file names,
  tenant names, or database identifiers.
- **Correlation:** entirely local, by request-array index; the chunk Guid never
  leaves OpsFlow.

Amazon Bedrock's documented posture is that third-party model providers do not
receive customer inputs/outputs through Bedrock, and that AWS does not use those
inputs/outputs to train the underlying base models. This is **not** an
unconditional zero-retention guarantee: model/service retention and
abuse-detection policies for the specific model and configuration must still be
reviewed. AWS PrivateLink can further constrain connectivity in a future
deployment; no such network topology is implemented in this PR.

Tenant isolation is structural and unchanged: candidates come only from
org+project-scoped hybrid retrieval, and the adapter cannot broaden scope or
introduce foreign ids.

### Cost context (dated design note, not runtime)

As of 2026-09-15, Cohere Rerank 3.5 on Bedrock is priced around US$2 per 1,000
rerank queries, where a billing search unit is one query plus up to 100
documents; OpsFlow reranks ≤ 50 candidates, so one rerank is one search unit. No
pricing constant exists in runtime code. Long candidate texts may trigger
provider-side chunking, which can increase the billable search units for a query
— a reason the operational capacity and candidate sizing matter.

### Evaluation

Normal CI stays network-free, secret-free, and deterministic: the existing
baseline-vs-candidate comparison (ADR-009) using the deterministic token-overlap
test reranker is unchanged. A **manual, opt-in** integration test exercises the
real Bedrock adapter over the same synthetic dataset, the same SQL hybrid
retrieval baseline, the same fair-pool proof, the same `RetrievalEvaluator`, and
the same metrics (Recall@K, MRR@K, nDCG@K for K = 1, 3, 5, 8). It runs only when
`OPSFLOW_RUN_REAL_RERANK_EVAL=true` (and valid AWS credentials/config are
present) and otherwise skips without contacting AWS. It is **report-only**: it
applies no quality threshold and never asserts the real reranker beats the
baseline. Real-world reranking quality is measured, not assumed.

### Not activated for users

`SearchDocumentChunksRerankedService` remains unregistered in production DI, and
`AnswerProjectQuestionService` and the grounded-RAG HTTP path are untouched. This
PR ships the adapter, its configuration/identity, DI for `IChunkReranker`, tests,
and this ADR only.

## Consequences

- A production reranker is available behind the unchanged `IChunkReranker` port,
  with AWS types confined to a single file, credentials via the AWS chain, and a
  documented privacy/egress boundary.
- The same evaluation harness now has a real-provider mode, gated so ordinary CI
  never calls AWS.
- **Limitations:** reranking is not activated for users; real-world quality is
  not yet measured (the manual evaluation must be run and reviewed); the
  operational capacity of 100 is a policy choice, not a tuned value; the privacy
  posture is Bedrock's documented behaviour, not an unconditional zero-retention
  guarantee.

## Alternatives considered

- **Reuse the existing OpenAI stack** — rejected: `OpenAI 2.13.0` exposes no
  genuine rerank/cross-encoder endpoint, and prompting a chat model to reorder
  chunks is not a real reranker.
- **Direct Cohere/Voyage/Jina APIs** — rejected for this PR: each adds a separate
  vendor API key and trust boundary; Bedrock keeps inference within the existing
  AWS/IAM boundary with no vendor API key.
- **Local ONNX cross-encoder** — deferred: strongest privacy but a heavy model
  asset, tokenizer parity, and deployment/CI complexity not justified now.
- **Storing a full Model ARN in configuration** — rejected in favour of Region +
  ModelId to prevent client/ARN region drift.
- **Activating reranking in this PR** — deferred: activation and the
  fail-open/fail-closed user-facing decision belong to a later PR with
  observability.
