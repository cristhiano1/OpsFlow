namespace OpsFlow.Application.Documents;

/// <summary>Distinguishes the outcomes of the extract-document-text use case.</summary>
public enum ExtractDocumentTextStatus
{
    /// <summary>Extraction was performed and persisted for the first time.</summary>
    SuccessCreated,

    /// <summary>A cached extraction already existed and was returned.</summary>
    SuccessExisting,

    /// <summary>The document, project, or tenant scope did not match any record.</summary>
    NotFound,

    /// <summary>Authorized metadata exists but the physical storage object is missing.</summary>
    StorageMissing,

    /// <summary>No registered extractor supports the document's content type.</summary>
    UnsupportedFormat,

    /// <summary>The document bytes are malformed or unreadable by the parser.</summary>
    MalformedDocument,

    /// <summary>The extracted text exceeds the configured character limit.</summary>
    ExtractionLimitExceeded,
}
