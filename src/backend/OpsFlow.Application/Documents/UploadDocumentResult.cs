using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of the upload-document use case. Distinguishes success, project-not-found,
/// and validation errors.
/// </summary>
public sealed class UploadDocumentResult
{
    /// <summary>Whether the target project was found in the caller's organization.</summary>
    public bool ProjectFound { get; private set; }

    /// <summary>Whether the upload completed successfully.</summary>
    public bool Succeeded { get; private set; }

    /// <summary>Validation error detail, when applicable.</summary>
    public string? Error { get; private set; }

    /// <summary>The persisted document metadata, when successful.</summary>
    public Document? Document { get; private set; }

    private UploadDocumentResult() { }

    /// <summary>Creates a successful result containing the persisted document.</summary>
    public static UploadDocumentResult Success(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new UploadDocumentResult { ProjectFound = true, Succeeded = true, Document = document };
    }

    /// <summary>Project does not exist or belongs to a different organization.</summary>
    public static UploadDocumentResult ProjectNotFound() =>
        new() { ProjectFound = false };

    /// <summary>The upload was rejected by business validation.</summary>
    public static UploadDocumentResult ValidationError(string error) =>
        new() { ProjectFound = true, Succeeded = false, Error = error };
}
