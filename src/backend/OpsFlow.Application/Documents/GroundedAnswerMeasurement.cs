namespace OpsFlow.Application.Documents;

/// <summary>
/// One request's worth of grounded-answer observability data. Every field is
/// bounded and provider-neutral: enums, counts, and durations only. It deliberately
/// carries <b>no</b> string content — no question, answer, evidence, prompt,
/// citation, or identifier — so it is impossible, by construction, to emit user or
/// document content or high-cardinality tags through this type (see ADR-012).
/// </summary>
/// <param name="Outcome">The bounded request outcome.</param>
/// <param name="RetrievalMode">Which retrieval path produced the evidence.</param>
/// <param name="FailureCategory">
/// The bounded failure category; <see cref="AnswerFailureCategory.None"/> unless
/// <paramref name="Outcome"/> is <see cref="AnswerPipelineOutcome.Failed"/>.
/// </param>
/// <param name="SelectedEvidenceCount">
/// The number of bounded evidence chunks supplied to the generator, or
/// <see langword="null"/> when evidence selection never occurred (project not found,
/// zero evidence, or a failure before selection).
/// </param>
/// <param name="TotalDuration">Wall-clock duration of the whole answer request.</param>
/// <param name="RetrievalDuration">Wall-clock duration of the retrieval phase.</param>
/// <param name="GenerationDuration">
/// Wall-clock duration of the generation phase, or <see langword="null"/> when the
/// generator was never invoked.
/// </param>
public readonly record struct GroundedAnswerMeasurement(
    AnswerPipelineOutcome Outcome,
    AnswerRetrievalMode RetrievalMode,
    AnswerFailureCategory FailureCategory,
    int? SelectedEvidenceCount,
    TimeSpan TotalDuration,
    TimeSpan RetrievalDuration,
    TimeSpan? GenerationDuration);
