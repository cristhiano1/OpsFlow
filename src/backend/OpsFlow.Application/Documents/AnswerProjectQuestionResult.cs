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

    private AnswerProjectQuestionResult() { }

    /// <summary>Creates a successful result carrying the grounded answer.</summary>
    public static AnswerProjectQuestionResult Success(GroundedAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return new AnswerProjectQuestionResult
        {
            Status = AnswerProjectQuestionStatus.Success,
            Answer = answer,
        };
    }

    /// <summary>
    /// Creates a not-found result. Used when the project does not exist or
    /// belongs to a different organization.
    /// </summary>
    public static AnswerProjectQuestionResult ProjectNotFound() =>
        new() { Status = AnswerProjectQuestionStatus.ProjectNotFound };

    /// <summary>
    /// Creates an insufficient-evidence result. Used when retrieval yields no
    /// evidence or the generator declares the evidence insufficient.
    /// </summary>
    public static AnswerProjectQuestionResult InsufficientEvidence() =>
        new() { Status = AnswerProjectQuestionStatus.InsufficientEvidence };
}
