# ADR-012: Grounded RAG observability foundation

- **Status:** Accepted
- **Date:** 2026-09-17
- **Context from:** [ADR-007](ADR-007-grounded-rag-answer-generation.md),
  [ADR-009](ADR-009-rag-reranking-foundation.md),
  [ADR-011](ADR-011-grounded-rag-reranking-activation.md)

## Context

The grounded `/answer` pipeline (hybrid/reranked retrieval → evidence bounding →
generation → verified citations) had no operational instrumentation. Operators
cannot yet answer basic questions: request volume, outcome mix, how often each
retrieval mode is used, how often reranking degrades to hybrid, latency of the
pipeline and its phases, how much evidence is selected, and how many failures occur
by category. This ADR adds provider-neutral instrumentation **without** changing any
external behavior of the pipeline, and **without** requiring an external telemetry
collector in this PR.

## Decision

### Instrumentation boundary (Application port, BCL implementation)

The Application layer defines a small provider-neutral port,
`IGroundedAnswerTelemetry`, with a single method `Record(in GroundedAnswerMeasurement)`.
`AnswerProjectQuestionService` calls it exactly once per request. The concrete
implementation, `MeterGroundedAnswerTelemetry`, lives in Infrastructure and uses the
BCL `System.Diagnostics.Metrics` API (a named `Meter`). The Application layer takes
**no** dependency on any metrics/telemetry SDK, configuration, or HTTP type — it
gains no new package reference at all.

Alternatives considered:

- **BCL `Meter`/`ActivitySource` directly in the Application service** — rejected:
  it would place a static instrumentation surface in the Application layer and make
  the privacy guarantee a matter of discipline rather than construction.
- **Composition-root decorator around the use case** — rejected: an outer decorator
  can observe inputs/outputs but cannot measure the internal retrieval-vs-generation
  phase boundaries the goal requires.

The port was chosen because it (1) makes privacy **structural**, (2) keeps the
Application layer dependency-free, (3) is trivially testable with a recording fake,
and (4) leaves exporter choice entirely outside the Application layer.

### Privacy and cardinality boundary

`GroundedAnswerMeasurement` carries **only** bounded, provider-neutral values:
outcome, retrieval mode, failure category (all enums), a nullable selected-evidence
count, and three durations. It has **no string or identifier field**, so it is
impossible by construction to emit question, answer, evidence, prompt, citation, or
any org/project/document/chunk/user identifier, credential, or exception message.
Instrument tags are limited to three bounded keys — `result.status`,
`retrieval.mode`, `error.category` — whose values come from fixed enums, never from
arbitrary strings.

### Metric surface

Meter `OpsFlow.Rag`:

| Instrument | Type | Unit | Tags |
| --- | --- | --- | --- |
| `opsflow.rag.answer.requests` | Counter&lt;long&gt; | `{request}` | `result.status`, `retrieval.mode` |
| `opsflow.rag.answer.failures` | Counter&lt;long&gt; | `{failure}` | `error.category` |
| `opsflow.rag.answer.duration` | Histogram&lt;double&gt; | `s` | `result.status`, `retrieval.mode` |
| `opsflow.rag.retrieval.duration` | Histogram&lt;double&gt; | `s` | `retrieval.mode` |
| `opsflow.rag.generation.duration` | Histogram&lt;double&gt; | `s` | — |
| `opsflow.rag.evidence.selected` | Histogram&lt;int&gt; | `{chunk}` | — |

Reranking operational fallbacks are **not** a separate counter: they are exactly the
`requests` series filtered by `retrieval.mode = hybrid_fallback`, so a dedicated
counter would duplicate an existing signal. Generation duration and selected-evidence
are recorded **only** when those phases actually ran.

### Retrieval-mode semantics

The existing `AnswerRetrievalMode` (`Hybrid` / `Reranked` / `HybridFallback` /
`NotApplicable`) is reused unchanged and consumed only as an internal telemetry tag.
It remains **absent** from the public HTTP contract. Retrieval semantics were not
altered to make telemetry easier.

### Failure taxonomy

Failures map to a bounded `AnswerFailureCategory`, derived from the Application
exception taxonomy (never from an exception message):

- `EmbeddingProviderFailure` — `EmbeddingGenerationException`
- `AnswerProviderFailure` — `AnswerGenerationException`
- `AnswerValidation` — `GroundedAnswerValidationException`
- `RerankerValidation` — `ChunkRerankingValidationException`
- `RerankerOperational` — `ChunkRerankingException`
- `Internal` — anything else

### Cancellation policy

Caller cancellation (`OperationCanceledException`) is reported as
`AnswerPipelineOutcome.Canceled` with `error.category = none`. It is **not** counted
as a system failure and never appears on the failures counter.

### Timing correctness

Total, retrieval, and (when it runs) generation durations are captured with
`Stopwatch` timestamps and `finally` blocks so a mid-phase failure still reports the
time actually spent. Generation duration is reported only when the generator was
invoked; the zero-evidence and project-not-found short circuits report no generation
duration and no selected-evidence value. Operational reranker fallback still produces
one coherent request-level measurement (outcome `Answered`, mode `HybridFallback`).
The exactly-once generator invariant is preserved; telemetry adds no generator call.

### Default-OFF / provider-graph invariant

Telemetry does not resolve or construct the reranker/provider graph. Under the
default `HybridOnly` policy the reranked service and the Bedrock provider are still
never resolved (ADR-011), and the existing regression test
`Answer_default_off_does_not_construct_or_invoke_reranker_provider` remains green.

### Exporter neutrality / no external collector

The implementation creates instruments only. Any exporter (OpenTelemetry OTLP,
Prometheus, etc.) attaches to the `OpsFlow.Rag` meter by name, out of process, in a
later PR. Normal CI stays deterministic, secret-free, and free of any OpenAI/AWS or
external-collector dependency: the instruments exist and are unit-tested with an
in-process `MeterListener`; no collector is required.

### Robustness

A misbehaving telemetry sink must never affect answering. The service wraps the
`Record` call and swallows any exception from it, so an exporter fault cannot break
the answer path.

## Consequences

- The grounded answer pipeline is measurable (volume, outcome/mode mix, fallback
  rate, phase latencies, selected evidence, failures by category) with a small,
  coherent metric surface and structural privacy guarantees.
- No public API change, no DTO change, no database/schema change, no new Application
  dependency, and no external monitoring infrastructure requirement.

## Limitations

- **No distributed tracing in this PR.** `ActivitySource` spans were deliberately
  deferred: the histograms already answer the latency questions, and real spans would
  require inlining an `ActivitySource` into the Application service (a second
  instrumentation mechanism) or expanding the port with start/stop scopes — added
  surface for marginal benefit in this instrumentation-foundation PR. A future PR may
  add an `opsflow.rag.answer` span with `retrieval` and `generation` children, using
  the same bounded attributes.
- **No dashboards, alerting, exporter, or persistence exist.** This PR establishes
  instrumentation only; it makes no claim of production monitoring.
- No real-provider quality metrics are produced; reranking remains default-OFF.
