namespace OpsFlow.Contracts.Documents;

/// <summary>
/// Stable API response for a grounded project answer. <see cref="Status"/> is a
/// stable external string — <c>"answered"</c> or <c>"insufficient_evidence"</c>.
/// <see cref="Answer"/> is non-null only when answered; <see cref="Citations"/>
/// is empty when the evidence was insufficient. Citations are ordered as the
/// grounding layer ordered them (first occurrence).
/// </summary>
/// <param name="Status">Outcome discriminator: "answered" or "insufficient_evidence".</param>
/// <param name="Answer">The grounded answer text, or null when insufficient evidence.</param>
/// <param name="Citations">Verified citations supporting the answer; empty when insufficient.</param>
public sealed record AnswerProjectQuestionResponse(
    string Status,
    string? Answer,
    IReadOnlyList<GroundedCitationResponse> Citations);
