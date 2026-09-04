namespace OpsFlow.Application.Documents;

/// <summary>
/// A single chunk returned by hybrid retrieval with its fused RRF score.
/// Source-specific metrics (CosineDistance, FtsRank) are intentionally excluded;
/// only 1-based rank positions from each source are retained for internal
/// observability.
/// </summary>
/// <param name="DocumentId">The document this chunk belongs to.</param>
/// <param name="DocumentChunkId">The chunk's unique identifier.</param>
/// <param name="ChunkIndex">Zero-based position of this chunk within the document.</param>
/// <param name="StartOffset">Inclusive UTF-16 code-unit offset into the extraction text.</param>
/// <param name="EndOffset">Exclusive UTF-16 code-unit offset into the extraction text.</param>
/// <param name="Text">Exact persisted chunk text.</param>
/// <param name="RrfScore">Reciprocal Rank Fusion score. Higher is more relevant.</param>
/// <param name="SemanticRank">1-based rank in the semantic source, or null if absent.</param>
/// <param name="LexicalRank">1-based rank in the lexical source, or null if absent.</param>
public sealed record HybridChunkHit(
    Guid DocumentId,
    Guid DocumentChunkId,
    int ChunkIndex,
    int StartOffset,
    int EndOffset,
    string Text,
    double RrfScore,
    int? SemanticRank,
    int? LexicalRank);
