namespace OpsFlow.Application.Documents;

/// <summary>
/// Bounded, provider-neutral category for a grounded-answer pipeline failure. The
/// value is derived from the Application exception taxonomy — never from an
/// exception message — so telemetry stays low-cardinality and free of provider or
/// content detail (see ADR-012). Caller cancellation is intentionally not a failure
/// category; it is reported through <see cref="AnswerPipelineOutcome.Canceled"/>.
/// </summary>
public enum AnswerFailureCategory
{
    /// <summary>No failure occurred (the request produced a result).</summary>
    None = 0,

    /// <summary>The embedding provider failed during retrieval (operational).</summary>
    EmbeddingProviderFailure = 1,

    /// <summary>The reranker was operationally unavailable (operational).</summary>
    RerankerOperational = 2,

    /// <summary>The reranker returned malformed/untrusted output (fail closed).</summary>
    RerankerValidation = 3,

    /// <summary>The answer-generation provider failed (operational).</summary>
    AnswerProviderFailure = 4,

    /// <summary>The generated answer violated the grounding contract (fail closed).</summary>
    AnswerValidation = 5,

    /// <summary>An internal invariant violation or otherwise unexpected failure.</summary>
    Internal = 6,
}
