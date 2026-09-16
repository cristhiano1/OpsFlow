namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of the grounded answer-generation use case. <see cref="Answer"/> is
/// non-null only when <see cref="Status"/> is
/// <see cref="AnswerProjectQuestionStatus.Success"/>.
/// </summary>
public sealed class AnswerProjectQuestionResult
{
    /// <summary>The outcome of the use case.</summary>
    public AnswerProjectQuestionStatus Status { get; private set; }

    /// <summary>
    /// The grounded answer. Non-null if and only if <see cref="Status"/> is
    /// <see cref="AnswerProjectQuestionStatus.Success"/>.
    /// </summary>
    public GroundedAnswer? Answer { get; private set; }

    /// <summary>
    /// Provider-neutral record of how evidence was actually retrieved. Application
    /// observability only; not exposed on the public HTTP contract.
    /// </summary>
    public AnswerRetrievalMode RetrievalMode { get; private set; }

    private AnswerProjectQuestionResult() { }

    /// <summary>Creates a successful result carrying the grounded answer and the retrieval mode used.</summary>
    public static AnswerProjectQuestionResult Success(GroundedAnswer answer, AnswerRetrievalMode retrievalMode)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return new AnswerProjectQuestionResult
        {
            Status = AnswerProjectQuestionStatus.Success,
            Answer = answer,
            RetrievalMode = retrievalMode,
        };
    }

    /// <summary>
    /// Creates a not-found result. Used when the project does not exist or
    /// belongs to a different organization. Retrieval mode is
    /// <see cref="AnswerRetrievalMode.NotApplicable"/>.
    /// </summary>
    public static AnswerProjectQuestionResult ProjectNotFound() =>
        new() { Status = AnswerProjectQuestionStatus.ProjectNotFound, RetrievalMode = AnswerRetrievalMode.NotApplicable };

    /// <summary>
    /// Creates an insufficient-evidence result. Used when retrieval yields no
    /// evidence (mode <see cref="AnswerRetrievalMode.NotApplicable"/>) or the
    /// generator declares supplied evidence insufficient (mode preserves the path
    /// that actually produced that evidence).
    /// </summary>
    public static AnswerProjectQuestionResult InsufficientEvidence(AnswerRetrievalMode retrievalMode) =>
        new() { Status = AnswerProjectQuestionStatus.InsufficientEvidence, RetrievalMode = retrievalMode };
}
