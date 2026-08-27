namespace OpsFlow.Domain.Documents;

/// <summary>
/// Immutable record of a completed embedding operation for a document's chunks.
/// One embedding set per (document, profile) pair; <see cref="Id"/> is the primary key
/// with a unique constraint on (<see cref="DocumentId"/>, <see cref="ProfileId"/>).
/// A completed artifact — partial sets are never persisted.
/// </summary>
public sealed class DocumentEmbeddingSet
{
    /// <summary>Maximum length for <see cref="ProfileId"/>.</summary>
    public const int MaxProfileIdLength = 100;

    /// <summary>Maximum length for <see cref="ModelId"/>.</summary>
    public const int MaxModelIdLength = 200;

    /// <summary>Unique identifier for this embedding set.</summary>
    public Guid Id { get; private set; }

    /// <summary>The document whose chunks were embedded.</summary>
    public Guid DocumentId { get; private set; }

    /// <summary>Chunking algorithm version of the source chunks.</summary>
    public int ChunkingVersion { get; private set; }

    /// <summary>Product embedding-space compatibility identity (e.g. "opsflow-semantic-v1").</summary>
    public string ProfileId { get; private set; } = string.Empty;

    /// <summary>Provider/model audit identity (e.g. "text-embedding-3-small").</summary>
    public string ModelId { get; private set; } = string.Empty;

    /// <summary>Number of float dimensions per vector.</summary>
    public int Dimensions { get; private set; }

    /// <summary>Total number of embedding rows. Must equal source chunk count.</summary>
    public int EmbeddingCount { get; private set; }

    /// <summary>UTC timestamp when the embeddings were generated.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    private DocumentEmbeddingSet() { }

    /// <summary>Creates a new embedding set with validated invariants.</summary>
    public DocumentEmbeddingSet(
        Guid id,
        Guid documentId,
        int chunkingVersion,
        string profileId,
        string modelId,
        int dimensions,
        int embeddingCount,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("ID must not be empty.", nameof(id));
        }

        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Document ID must not be empty.", nameof(documentId));
        }

        if (chunkingVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkingVersion), "Chunking version must be >= 1.");
        }

        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("Profile ID must not be null, empty, or whitespace.", nameof(profileId));
        }

        if (profileId.Length > MaxProfileIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(profileId),
                $"Profile ID length ({profileId.Length}) exceeds maximum ({MaxProfileIdLength}).");
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("Model ID must not be null, empty, or whitespace.", nameof(modelId));
        }

        if (modelId.Length > MaxModelIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(modelId),
                $"Model ID length ({modelId.Length}) exceeds maximum ({MaxModelIdLength}).");
        }

        if (dimensions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Dimensions must be >= 1.");
        }

        if (embeddingCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(embeddingCount), "Embedding count must be >= 0.");
        }

        Id = id;
        DocumentId = documentId;
        ChunkingVersion = chunkingVersion;
        ProfileId = profileId;
        ModelId = modelId;
        Dimensions = dimensions;
        EmbeddingCount = embeddingCount;
        CreatedAt = createdAt;
    }
}
