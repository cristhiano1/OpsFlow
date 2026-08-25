using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of the extract-document-text use case. Factory methods enforce valid
/// combinations of status and extraction entity.
/// </summary>
public sealed class ExtractDocumentTextResult
{
    /// <summary>The outcome of the extraction attempt.</summary>
    public ExtractDocumentTextStatus Status { get; private set; }

    /// <summary>The extraction entity. Only meaningful for success statuses.</summary>
    public DocumentExtraction? Extraction { get; private set; }

    private ExtractDocumentTextResult() { }

    /// <summary>Extraction was performed and persisted for the first time.</summary>
    public static ExtractDocumentTextResult SuccessCreated(DocumentExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        return new ExtractDocumentTextResult
        {
            Status = ExtractDocumentTextStatus.SuccessCreated,
            Extraction = extraction,
        };
    }

    /// <summary>A cached extraction already existed.</summary>
    public static ExtractDocumentTextResult SuccessExisting(DocumentExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        return new ExtractDocumentTextResult
        {
            Status = ExtractDocumentTextStatus.SuccessExisting,
            Extraction = extraction,
        };
    }

    /// <summary>The document, project, or tenant scope did not match any record.</summary>
    public static ExtractDocumentTextResult NotFound() =>
        new() { Status = ExtractDocumentTextStatus.NotFound };

    /// <summary>Authorized metadata exists but the physical storage object is missing.</summary>
    public static ExtractDocumentTextResult StorageMissing() =>
        new() { Status = ExtractDocumentTextStatus.StorageMissing };

    /// <summary>No registered extractor supports the document's content type.</summary>
    public static ExtractDocumentTextResult UnsupportedFormat() =>
        new() { Status = ExtractDocumentTextStatus.UnsupportedFormat };

    /// <summary>The document bytes are malformed or unreadable by the parser.</summary>
    public static ExtractDocumentTextResult MalformedDocument() =>
        new() { Status = ExtractDocumentTextStatus.MalformedDocument };

    /// <summary>The extracted text exceeds the configured character limit.</summary>
    public static ExtractDocumentTextResult ExtractionLimitExceeded() =>
        new() { Status = ExtractDocumentTextStatus.ExtractionLimitExceeded };
}
