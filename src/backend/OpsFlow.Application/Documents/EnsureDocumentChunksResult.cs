using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of the ensure-document-chunks use case. Factory methods enforce valid
/// combinations of status and chunk set entity.
/// </summary>
public sealed class EnsureDocumentChunksResult
{
    /// <summary>The outcome of the chunking attempt.</summary>
    public EnsureDocumentChunksStatus Status { get; private set; }

    /// <summary>The chunk set entity. Only meaningful for success statuses.</summary>
    public DocumentChunkSet? ChunkSet { get; private set; }

    private EnsureDocumentChunksResult() { }

    /// <summary>Chunks were produced and persisted for the first time.</summary>
    public static EnsureDocumentChunksResult SuccessCreated(DocumentChunkSet chunkSet)
    {
        ArgumentNullException.ThrowIfNull(chunkSet);
        return new EnsureDocumentChunksResult
        {
            Status = EnsureDocumentChunksStatus.SuccessCreated,
            ChunkSet = chunkSet,
        };
    }

    /// <summary>A cached chunk set already existed.</summary>
    public static EnsureDocumentChunksResult SuccessExisting(DocumentChunkSet chunkSet)
    {
        ArgumentNullException.ThrowIfNull(chunkSet);
        return new EnsureDocumentChunksResult
        {
            Status = EnsureDocumentChunksStatus.SuccessExisting,
            ChunkSet = chunkSet,
        };
    }

    /// <summary>The document, project, or tenant scope did not match any record.</summary>
    public static EnsureDocumentChunksResult NotFound() =>
        new() { Status = EnsureDocumentChunksStatus.NotFound };

    /// <summary>The document exists but has no text extraction yet.</summary>
    public static EnsureDocumentChunksResult ExtractionNotFound() =>
        new() { Status = EnsureDocumentChunksStatus.ExtractionNotFound };
}
