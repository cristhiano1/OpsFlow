using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of the ensure-document-embeddings use case. Factory methods enforce
/// valid combinations of status and embedding set entity.
/// </summary>
public sealed class EnsureDocumentEmbeddingsResult
{
    /// <summary>The outcome of the embedding attempt.</summary>
    public EnsureDocumentEmbeddingsStatus Status { get; private set; }

    /// <summary>The embedding set entity. Only meaningful for success statuses.</summary>
    public DocumentEmbeddingSet? EmbeddingSet { get; private set; }

    private EnsureDocumentEmbeddingsResult() { }

    /// <summary>Embeddings were generated and persisted for the first time.</summary>
    public static EnsureDocumentEmbeddingsResult SuccessCreated(DocumentEmbeddingSet embeddingSet)
    {
        ArgumentNullException.ThrowIfNull(embeddingSet);
        return new EnsureDocumentEmbeddingsResult
        {
            Status = EnsureDocumentEmbeddingsStatus.SuccessCreated,
            EmbeddingSet = embeddingSet,
        };
    }

    /// <summary>A compatible embedding set already existed.</summary>
    public static EnsureDocumentEmbeddingsResult SuccessExisting(DocumentEmbeddingSet embeddingSet)
    {
        ArgumentNullException.ThrowIfNull(embeddingSet);
        return new EnsureDocumentEmbeddingsResult
        {
            Status = EnsureDocumentEmbeddingsStatus.SuccessExisting,
            EmbeddingSet = embeddingSet,
        };
    }

    /// <summary>The document, project, or tenant scope did not match any record.</summary>
    public static EnsureDocumentEmbeddingsResult NotFound() =>
        new() { Status = EnsureDocumentEmbeddingsStatus.NotFound };

    /// <summary>The document exists but has no chunk set yet.</summary>
    public static EnsureDocumentEmbeddingsResult ChunksNotFound() =>
        new() { Status = EnsureDocumentEmbeddingsStatus.ChunksNotFound };

    /// <summary>An existing embedding set has incompatible metadata.</summary>
    public static EnsureDocumentEmbeddingsResult InvariantConflict(DocumentEmbeddingSet embeddingSet)
    {
        ArgumentNullException.ThrowIfNull(embeddingSet);
        return new EnsureDocumentEmbeddingsResult
        {
            Status = EnsureDocumentEmbeddingsStatus.InvariantConflict,
            EmbeddingSet = embeddingSet,
        };
    }
}
