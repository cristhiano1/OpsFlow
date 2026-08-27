using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of an idempotent embedding set insert. Distinguishes between a
/// newly added set, a concurrent duplicate, and a tenant ownership failure.
/// </summary>
public sealed class DocumentEmbeddingSetAddResult
{
    /// <summary>The outcome of the insert attempt.</summary>
    public DocumentEmbeddingSetAddStatus Status { get; private set; }

    /// <summary>The embedding set entity (newly inserted or the pre-existing row).</summary>
    public DocumentEmbeddingSet? EmbeddingSet { get; private set; }

    private DocumentEmbeddingSetAddResult() { }

    /// <summary>The embedding set was newly inserted.</summary>
    public static DocumentEmbeddingSetAddResult Added(DocumentEmbeddingSet embeddingSet)
    {
        ArgumentNullException.ThrowIfNull(embeddingSet);
        return new DocumentEmbeddingSetAddResult
        {
            Status = DocumentEmbeddingSetAddStatus.Added,
            EmbeddingSet = embeddingSet,
        };
    }

    /// <summary>An embedding set for this document and profile already existed.</summary>
    public static DocumentEmbeddingSetAddResult AlreadyExists(DocumentEmbeddingSet embeddingSet)
    {
        ArgumentNullException.ThrowIfNull(embeddingSet);
        return new DocumentEmbeddingSetAddResult
        {
            Status = DocumentEmbeddingSetAddStatus.AlreadyExists,
            EmbeddingSet = embeddingSet,
        };
    }

    /// <summary>The document could not be found within the tenant scope.</summary>
    public static DocumentEmbeddingSetAddResult NotFound() =>
        new() { Status = DocumentEmbeddingSetAddStatus.NotFound };
}
