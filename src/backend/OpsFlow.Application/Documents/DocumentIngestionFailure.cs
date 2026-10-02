namespace OpsFlow.Application.Documents;

/// <summary>
/// Describes why the ingestion pipeline stopped after a successful upload.
/// </summary>
public enum DocumentIngestionFailure
{
    /// <summary>No failure — ingestion completed successfully.</summary>
    None,

    /// <summary>No registered extractor supports the document's content type.</summary>
    ExtractionUnsupportedFormat,

    /// <summary>The document bytes are malformed or unreadable by the parser.</summary>
    ExtractionMalformedDocument,

    /// <summary>The extracted text exceeds the configured character limit.</summary>
    ExtractionLimitExceeded,

    /// <summary>The physical storage object is missing after a successful upload.</summary>
    ExtractionStorageMissing,
}
