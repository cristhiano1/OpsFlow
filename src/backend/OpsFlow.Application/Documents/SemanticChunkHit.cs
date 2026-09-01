namespace OpsFlow.Application.Documents;

/// <summary>A single chunk returned by semantic retrieval, with its distance.</summary>
/// <param name="DocumentId">The document this chunk belongs to.</param>
/// <param name="DocumentChunkId">The chunk's unique identifier.</param>
/// <param name="ChunkIndex">Zero-based position of this chunk within the document.</param>
/// <param name="StartOffset">Inclusive UTF-16 code-unit offset into the extraction text.</param>
/// <param name="EndOffset">Exclusive UTF-16 code-unit offset into the extraction text.</param>
/// <param name="Text">Exact persisted chunk text.</param>
/// <param name="CosineDistance">Cosine distance from the query vector. Smaller is more similar.</param>
public sealed record SemanticChunkHit(
    Guid DocumentId,
    Guid DocumentChunkId,
    int ChunkIndex,
    int StartOffset,
    int EndOffset,
    string Text,
    double CosineDistance);
