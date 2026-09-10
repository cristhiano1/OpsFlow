namespace OpsFlow.Contracts.Documents;

/// <summary>
/// A single verified citation backing a grounded answer. Every field is
/// authoritative provenance drawn from the retrieved document chunk — never from
/// model output. No scoring, ranking, internal evidence label, or provider
/// detail is exposed; relevance/reference order is conveyed by position within
/// the containing collection.
/// </summary>
/// <param name="DocumentId">The document this chunk belongs to.</param>
/// <param name="DocumentChunkId">The chunk's unique identifier.</param>
/// <param name="ChunkIndex">Zero-based position of this chunk within the document.</param>
/// <param name="StartOffset">Inclusive UTF-16 code-unit offset into the extracted text.</param>
/// <param name="EndOffset">Exclusive UTF-16 code-unit offset into the extracted text.</param>
/// <param name="Text">The exact persisted chunk text.</param>
public sealed record GroundedCitationResponse(
    Guid DocumentId,
    Guid DocumentChunkId,
    int ChunkIndex,
    int StartOffset,
    int EndOffset,
    string Text);
