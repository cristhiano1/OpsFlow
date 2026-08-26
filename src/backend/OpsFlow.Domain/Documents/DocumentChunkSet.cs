namespace OpsFlow.Domain.Documents;

/// <summary>
/// Immutable record of a completed chunking operation for a <see cref="DocumentExtraction"/>.
/// One chunk set per document; <see cref="DocumentId"/> is the primary key.
/// A chunk set is a completed artifact — partial sets are never persisted.
/// </summary>
public sealed class DocumentChunkSet
{
    /// <summary>The document this chunk set belongs to (also the primary key).</summary>
    public Guid DocumentId { get; private set; }

    /// <summary>
    /// Algorithm version that produced these chunks. Must be &gt;= 1.
    /// Enables future re-chunking when the algorithm changes.
    /// </summary>
    public int ChunkingVersion { get; private set; }

    /// <summary>
    /// Total number of chunks produced. Zero is valid (empty extraction text).
    /// </summary>
    public int ChunkCount { get; private set; }

    /// <summary>UTC timestamp when the chunking was performed.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    private DocumentChunkSet() { }

    /// <summary>Creates a new chunk set with validated invariants.</summary>
    public DocumentChunkSet(Guid documentId, int chunkingVersion, int chunkCount, DateTimeOffset createdAt)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Document ID must not be empty.", nameof(documentId));
        }

        if (chunkingVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkingVersion), "Chunking version must be >= 1.");
        }

        if (chunkCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkCount), "Chunk count must be >= 0.");
        }

        DocumentId = documentId;
        ChunkingVersion = chunkingVersion;
        ChunkCount = chunkCount;
        CreatedAt = createdAt;
    }
}
