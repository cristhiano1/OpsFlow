namespace OpsFlow.Application.Documents;

/// <summary>Outcome of the grounded answer-generation use case.</summary>
public enum AnswerProjectQuestionStatus
{
    /// <summary>An evidence-grounded answer with verified citations was produced.</summary>
    Success,

    /// <summary>
    /// The project does not exist within the caller's organization. Returned for
    /// both nonexistent and cross-tenant projects — the two are indistinguishable.
    /// </summary>
    ProjectNotFound,

    /// <summary>
    /// No answer could be grounded: either hybrid retrieval returned no evidence
    /// (the generator was never invoked) or the generator declared the evidence
    /// insufficient.
    /// </summary>
    InsufficientEvidence,
}
