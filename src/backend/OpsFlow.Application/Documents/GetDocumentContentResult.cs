using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>Distinguishes the outcomes of a document content retrieval.</summary>
public enum GetDocumentContentStatus
{
    /// <summary>The document was found and the content stream is available.</summary>
    Success,

    /// <summary>The document, project, or tenant scope did not match any record.</summary>
    NotFound,

    /// <summary>Authorized metadata exists but the physical storage object is missing.</summary>
    StorageMissing,
}

/// <summary>
/// Result of the get-document-content use case. Factory methods enforce valid
/// combinations of status, metadata, and content stream.
/// </summary>
public sealed class GetDocumentContentResult
{
    /// <summary>The outcome of the retrieval attempt.</summary>
    public GetDocumentContentStatus Status { get; private set; }

    /// <summary>The document metadata. Only meaningful when <see cref="Status"/> is <see cref="GetDocumentContentStatus.Success"/>.</summary>
    public Document? Metadata { get; private set; }

    /// <summary>The open content stream. Only meaningful when <see cref="Status"/> is <see cref="GetDocumentContentStatus.Success"/>.</summary>
    public Stream? Content { get; private set; }

    private GetDocumentContentResult() { }

    /// <summary>Creates a successful result with metadata and an open content stream.</summary>
    public static GetDocumentContentResult Success(Document metadata, Stream content)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);
        return new GetDocumentContentResult
        {
            Status = GetDocumentContentStatus.Success,
            Metadata = metadata,
            Content = content,
        };
    }

    /// <summary>The document was not found (or inaccessible due to tenant/project scoping).</summary>
    public static GetDocumentContentResult NotFound() =>
        new() { Status = GetDocumentContentStatus.NotFound };

    /// <summary>Metadata exists but the physical storage object is missing.</summary>
    public static GetDocumentContentResult StorageMissing() =>
        new() { Status = GetDocumentContentStatus.StorageMissing };
}
