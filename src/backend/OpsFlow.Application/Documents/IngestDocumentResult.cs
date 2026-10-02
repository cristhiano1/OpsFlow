using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of the ingest-document use case. Covers upload validation,
/// project lookup, and downstream indexing outcomes.
/// </summary>
public sealed class IngestDocumentResult
{
    /// <summary>Whether the target project was found in the caller's organization.</summary>
    public bool ProjectFound { get; private set; }

    /// <summary>Whether the full ingestion (upload + extract + chunk + embed) completed.</summary>
    public bool Succeeded { get; private set; }

    /// <summary>Upload validation error detail, when applicable.</summary>
    public string? Error { get; private set; }

    /// <summary>The persisted document metadata. Present when the upload itself succeeded.</summary>
    public Document? Document { get; private set; }

    /// <summary>Describes why the indexing pipeline stopped, if it did.</summary>
    public DocumentIngestionFailure IngestionFailure { get; private set; }

    private IngestDocumentResult() { }

    /// <summary>Upload and full indexing pipeline completed.</summary>
    public static IngestDocumentResult FullyIngested(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new IngestDocumentResult
        {
            ProjectFound = true,
            Succeeded = true,
            Document = document,
            IngestionFailure = DocumentIngestionFailure.None,
        };
    }

    /// <summary>Project does not exist or belongs to a different organization.</summary>
    public static IngestDocumentResult ProjectNotFound() =>
        new() { ProjectFound = false, IngestionFailure = DocumentIngestionFailure.None };

    /// <summary>The upload was rejected by business validation.</summary>
    public static IngestDocumentResult UploadValidationFailed(string error) =>
        new() { ProjectFound = true, Error = error, IngestionFailure = DocumentIngestionFailure.None };

    /// <summary>Upload succeeded but an indexing step failed with a content-related issue.</summary>
    public static IngestDocumentResult IndexingFailed(Document document, DocumentIngestionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new IngestDocumentResult
        {
            ProjectFound = true,
            Document = document,
            IngestionFailure = failure,
        };
    }
}
