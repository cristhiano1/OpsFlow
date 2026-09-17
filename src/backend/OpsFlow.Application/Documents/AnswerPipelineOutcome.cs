namespace OpsFlow.Application.Documents;

/// <summary>
/// Provider-neutral, bounded outcome of a single grounded-answer request, used for
/// observability only. It never carries user or document content and is not part of
/// the public HTTP contract (see ADR-012).
/// </summary>
public enum AnswerPipelineOutcome
{
    /// <summary>An evidence-grounded answer with verified citations was produced.</summary>
    Answered = 0,

    /// <summary>Retrieval or the generator declared the available evidence insufficient.</summary>
    InsufficientEvidence = 1,

    /// <summary>The project did not exist or belonged to a different organization.</summary>
    ProjectNotFound = 2,

    /// <summary>The pipeline failed with an exception (see <see cref="AnswerFailureCategory"/>).</summary>
    Failed = 3,

    /// <summary>The caller cancelled the request; not counted as a system failure.</summary>
    Canceled = 4,
}
