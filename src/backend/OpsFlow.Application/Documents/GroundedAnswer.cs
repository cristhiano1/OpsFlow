namespace OpsFlow.Application.Documents;

/// <summary>
/// An evidence-grounded answer. <see cref="Text"/> is the generated answer with
/// no inline citation markers; <see cref="Citations"/> is the authoritative,
/// deduplicated list of supporting evidence, ordered by first occurrence in the
/// generator's citation array.
/// </summary>
/// <param name="Text">The generated answer text.</param>
/// <param name="Citations">Verified citations, each tied to a retrieved chunk.</param>
public sealed record GroundedAnswer(
    string Text,
    IReadOnlyList<GroundedCitation> Citations);
