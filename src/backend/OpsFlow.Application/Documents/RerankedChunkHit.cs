namespace OpsFlow.Application.Documents;

/// <summary>
/// A single chunk returned by reranked retrieval. All chunk metadata is
/// authoritative — projected from the retrieved <see cref="HybridChunkHit"/>,
/// never from the reranker. The reranker contributes only <see cref="RerankScore"/>;
/// <see cref="OriginalHybridRank"/> records the chunk's 1-based position in the
/// hybrid (RRF) result before reranking, retained for observability and as the
/// deterministic tie-break.
/// </summary>
/// <param name="DocumentId">The document this chunk belongs to.</param>
/// <param name="DocumentChunkId">The chunk's unique identifier.</param>
/// <param name="ChunkIndex">Zero-based position of this chunk within the document.</param>
/// <param name="StartOffset">Inclusive UTF-16 code-unit offset into the extraction text.</param>
/// <param name="EndOffset">Exclusive UTF-16 code-unit offset into the extraction text.</param>
/// <param name="Text">Exact persisted chunk text.</param>
/// <param name="RerankScore">The reranker's relevance score. Higher is more relevant.</param>
/// <param name="OriginalHybridRank">1-based rank in the hybrid (RRF) result before reranking.</param>
public sealed record RerankedChunkHit(
    Guid DocumentId,
    Guid DocumentChunkId,
    int ChunkIndex,
    int StartOffset,
    int EndOffset,
    string Text,
    double RerankScore,
    int OriginalHybridRank);
