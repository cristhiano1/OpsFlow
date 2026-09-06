namespace OpsFlow.Application.Documents;

/// <summary>
/// A single verified citation. Every field except <see cref="CitationNumber"/>
/// originates exclusively from a retrieved <see cref="HybridChunkHit"/> — the
/// answer generator only ever supplies the temporary integer label. The model
/// can never author authoritative provenance (document/chunk identifiers,
/// offsets, or text).
/// </summary>
/// <param name="CitationNumber">1-based evidence label assigned by OpsFlow.</param>
/// <param name="DocumentId">The document this chunk belongs to.</param>
/// <param name="DocumentChunkId">The chunk's unique identifier.</param>
/// <param name="ChunkIndex">Zero-based position of this chunk within the document.</param>
/// <param name="StartOffset">Inclusive UTF-16 code-unit offset into the extraction text.</param>
/// <param name="EndOffset">Exclusive UTF-16 code-unit offset into the extraction text.</param>
/// <param name="Text">Exact persisted chunk text (never a model-authored excerpt).</param>
public sealed record GroundedCitation(
    int CitationNumber,
    Guid DocumentId,
    Guid DocumentChunkId,
    int ChunkIndex,
    int StartOffset,
    int EndOffset,
    string Text);
