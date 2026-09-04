namespace OpsFlow.Application.Documents;

/// <summary>A single chunk returned by lexical full-text retrieval, with its rank.</summary>
/// <param name="DocumentId">The document this chunk belongs to.</param>
/// <param name="DocumentChunkId">The chunk's unique identifier.</param>
/// <param name="ChunkIndex">Zero-based position of this chunk within the document.</param>
/// <param name="StartOffset">Inclusive UTF-16 code-unit offset into the extraction text.</param>
/// <param name="EndOffset">Exclusive UTF-16 code-unit offset into the extraction text.</param>
/// <param name="Text">Exact persisted chunk text.</param>
/// <param name="FtsRank">SQL Server internal relevance rank. Higher is more relevant. Internal only.</param>
public sealed record LexicalChunkHit(
    Guid DocumentId,
    Guid DocumentChunkId,
    int ChunkIndex,
    int StartOffset,
    int EndOffset,
    string Text,
    int FtsRank);
