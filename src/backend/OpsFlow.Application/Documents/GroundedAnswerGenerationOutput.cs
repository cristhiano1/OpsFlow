namespace OpsFlow.Application.Documents;

/// <summary>The status the generator declared for a completion.</summary>
public enum GeneratedAnswerStatus
{
    /// <summary>The model produced an evidence-grounded answer.</summary>
    Answered,

    /// <summary>The model declared the supplied evidence insufficient to answer.</summary>
    InsufficientEvidence,
}

/// <summary>
/// Provider-neutral output from <see cref="IGroundedAnswerGenerator"/>. Carries
/// only the declared status, the raw answer text, and the raw model-declared
/// citation labels. It contains no chunk metadata, identifiers, or provider
/// details — grounding validation and citation mapping happen in the
/// Application layer.
/// </summary>
/// <param name="Status">The status the model declared.</param>
/// <param name="Answer">Answer text (null when insufficient evidence).</param>
/// <param name="CitationNumbers">Raw, unverified 1-based evidence labels the model cited.</param>
public sealed record GroundedAnswerGenerationOutput(
    GeneratedAnswerStatus Status,
    string? Answer,
    IReadOnlyList<int> CitationNumbers);
