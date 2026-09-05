namespace OpsFlow.Contracts.Documents;

/// <summary>
/// A single document chunk returned by a search. Relevance is conveyed by
/// position within the containing collection — no scoring fields are exposed.
/// </summary>
/// <param name="DocumentId">The document this chunk belongs to.</param>
/// <param name="DocumentChunkId">The chunk's unique identifier.</param>
/// <param name="ChunkIndex">Zero-based position of this chunk within the document.</param>
/// <param name="StartOffset">Inclusive UTF-16 code-unit offset into the extracted text.</param>
/// <param name="EndOffset">Exclusive UTF-16 code-unit offset into the extracted text.</param>
/// <param name="Text">The chunk text.</param>
public sealed record SearchDocumentChunkHitResponse(
    Guid DocumentId,
    Guid DocumentChunkId,
    int ChunkIndex,
    int StartOffset,
    int EndOffset,
    string Text);
