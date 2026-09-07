# ADR-007: Grounded RAG answer generation

- **Status:** Accepted
- **Date:** 2026-09-06
- **Context from:** [ADR-004](ADR-004-embedding-profile-v1.md),
  [ADR-005](ADR-005-lexical-retrieval-full-text-search.md),
  [ADR-006](ADR-006-hybrid-retrieval-rrf.md)

## Context

Hybrid retrieval (ADR-006) returns the most relevant document chunks for a
query but does not answer questions. This ADR introduces the internal,
provider-neutral foundation that turns a user question plus retrieved evidence
into a grounded answer with verified citations. It is Application-layer
orchestration only — no HTTP endpoint, persistence, streaming, or history. A
later PR exposes it over HTTP.

The core risk is trust. A language model is probabilistic and can fabricate
facts, identifiers, and citations, and uploaded documents can contain adversarial
instructions ("ignore previous instructions…"). The design must guarantee, by
construction, that no model-authored value ever becomes authoritative
provenance, and must reduce (without claiming to eliminate) the risk of the
model following instructions embedded in documents.

## Decision

### Composition over duplication

`AnswerProjectQuestionService` (Application) depends on the concrete
`SearchDocumentChunksHybridService` and a provider-neutral
`IGroundedAnswerGenerator`. Retrieval, query-text validation, organization
scoping, and the cross-tenant-indistinguishable `ProjectNotFound` semantics are
reused unchanged — no retrieval logic is duplicated, and no interface is
introduced solely to mock the hybrid service (its four underlying ports are
already fakeable).

### Application owns the grounding policy and prompt

Grounding and citation rules are product semantics, so the Application layer
owns the static system prompt, the evidence labeling, and the injection-resistant
prompt formatting. The Infrastructure adapter owns only provider interaction and
response parsing. The generator port exchanges two neutral prompt strings and a
neutral output record; no OpenAI, HTTP, or JSON types cross into Application.

### Fixed evidence bound

Evidence retrieval uses a fixed `EvidenceTopK = 8` (the final fused count, not
the hybrid candidate depth of 50). Selected evidence is a ranked prefix bounded
by `MaxEvidenceChars = 12_000` source characters: chunks are added in relevance
order until the next would exceed the budget, at which point selection stops
(it does not skip ahead to a smaller lower-ranked chunk); the first chunk is
always included even if it alone exceeds the budget. Evidence labels are 1-based
and contiguous over the selected set. Neither value is user-configurable in this
foundation.

### Strict structured output

The generator requests strict JSON-schema structured output (schema
`opsflow_grounded_answer_v1`): `{ status: "answered" | "insufficient_evidence",
answer: string | null, citations: integer[] }`. The Infrastructure adapter only
parses the provider representation into the neutral output; all semantic and
range validation happens in Application. The dynamic evidence count is not
encoded in the schema.

### Citations array is the only citation channel

The model references evidence exclusively through the structured `citations`
array. The system prompt forbids inline markers (`[1]`, footnotes, ids) in the
answer text, and — as fail-closed defense-in-depth — an answered result whose
answer text contains a bracketed reference to a supplied evidence id (`[1]`,
`[^1]`, `[1, 2]`, `[1; 2]`, `[1-2]`, `[1–2]`) is rejected with a
grounding-validation error rather than published. Range syntax is never expanded
into citations; for fail-closed validation only, a numeric dash range is
rejected when its interval overlaps the supplied temporary evidence-id range —
so `[0-9]` is rejected when ids `1..8` exist even though neither endpoint is
itself a valid id, while a wholly out-of-range range such as `[2026-2027]`
remains ordinary content. This is rejection only: the code never extracts, maps,
or enumerates citations from free text, so the structured array stays the sole
source of truth.
Bracketed numbers outside the evidence range (`[2026]`, markdown links) are
ordinary content and are left untouched, avoiding false positives. Citations are
deduplicated preserving first occurrence in the array, then each label `N` is
mapped to `selectedEvidence[N-1]`.

### Citation trust model

`GroundedCitation` is constructed exclusively from the retrieved
`HybridChunkHit`: the model supplies only the integer label, which is validated
to `[1, N]` and used as an index. There is no code path by which a
model-authored `DocumentId`, `DocumentChunkId`, `ChunkIndex`, offset, or text
can enter a citation. Citation text is the exact persisted chunk text; XML
escaping is applied only for prompt transport, never to the returned provenance.

### Prompt-injection defense-in-depth

Defenses are structural: system/user role separation, the question and every
evidence text XML-escaped, evidence wrapped in OpsFlow-generated `<evidence
id="N">` blocks, and a system policy that declares evidence untrusted data and
forbids following instructions inside it. **These defenses reduce risk but do
not make it impossible for a probabilistic model to follow malicious
instructions embedded in a document.**

### Question and source preservation

The original question is forwarded to hybrid retrieval unchanged (no trimming,
rewriting, normalization, or translation — query rewriting is future work).
Escaping in the prompt is serialization only; returned citation text is the
exact `HybridChunkHit.Text`.

### Insufficient evidence and call count

If retrieval returns zero hits, the generator is never called and the result is
`InsufficientEvidence`. If the model declares `insufficient_evidence`, the
result is `InsufficientEvidence` and the output must carry no answer and no
citations (fail closed otherwise). On the success path, hybrid retrieval runs
once and the generator runs once — there is no Application-level retry. Standard
SDK transport retry (429/5xx) is unchanged.

### Language and knowledge boundary

The system prompt instructs the model to honor an explicit output-language or
translation instruction in the question (a legitimate task instruction), falling
back to the language of the question when none is requested; a language
instruction inside evidence is untrusted and ignored, and it never overrides the
grounding/security rules. It also instructs the model to use only the supplied
evidence for factual claims, returning insufficient-evidence rather than
inventing unsupported facts.

### Two exception types

`AnswerGenerationException` (provider-neutral) covers transport/SDK failures,
invalid or truncated provider responses, and refusals. `GroundedAnswerValidationException`
covers well-formed output that violates the grounding contract (out-of-range or
missing citations, answered-with-no-answer, insufficient-with-content). Provider
and grounding failures propagate as exceptions; they are not business result
statuses. Cross-tenant/nonexistent projects and zero/insufficient evidence are
statuses, not exceptions.

### Configurable model

The chat model is configurable via `OpenAI:AnswerModel` (default `gpt-4o-mini`),
sharing the existing `OpenAI:ApiKey`. Generation uses `Temperature = 0` and
`MaxOutputTokenCount = 800`; no other sampling parameters are set. A missing key
or model fails with a clear message that never contains the secret value; no
prompt, evidence, or answer text is logged.

## Consequences

- **Grounded answers are available** as an internal Application service with
  verified, tamper-proof citations.
- **No HTTP endpoint, persistence, history, streaming, reranking, tools, or
  evaluation** — all are future work.
- **The model cannot forge provenance**; the worst a misbehaving model can do is
  produce an answer that is rejected by grounding validation or that cites the
  wrong (but real, retrieved) chunk.
- **Prompt injection remains possible in principle.** The structural defenses
  are a mitigation, not a guarantee; a future reranking/verification pass or
  output-side checks can strengthen it.
- **Fixed `EvidenceTopK = 8` and `MaxEvidenceChars = 12_000`** are unevaluated
  defaults; tuning requires evaluation infrastructure not yet built.

## Alternatives considered

- **Free-text `[N]` citation parsing**: fragile (false positives on years,
  markdown links) and creates two competing citation channels. Rejected in favor
  of the structured array as the single source of truth.
- **Letting the model return citation metadata (ids/offsets/quotes)**: would let
  the model author authoritative provenance. Rejected outright — the model
  returns only integer labels.
- **An interface wrapper around the hybrid service purely for testing**: the
  hybrid service's own ports are already fakeable, so a wrapper would be churn
  without architectural benefit. Rejected.
- **Discarding contradictory model output silently** (e.g. an insufficient
  result that carries an answer): rejected in favor of failing closed with a
  grounding-validation exception.
- **A prompt/model profile identity** (à la the embedding profile): nothing about
  the prompt or model is persisted in this foundation, so an identity would be
  unused ceremony. Deferred until answers or conversations are persisted.
