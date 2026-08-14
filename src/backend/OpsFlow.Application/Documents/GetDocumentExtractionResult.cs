using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of the get-document-extraction use case (read-only). The GET
/// endpoint never triggers extraction — it only reads persisted results.
/// </summary>
public sealed class GetDocumentExtractionResult
{
    /// <summary>Whether the extraction was found.</summary>
    public bool Found { get; private set; }

    /// <summary>The extraction entity. Only meaningful when <see cref="Found"/> is <c>true</c>.</summary>
    public DocumentExtraction? Extraction { get; private set; }

    private GetDocumentExtractionResult() { }

    /// <summary>Creates a successful result with the extraction entity.</summary>
    public static GetDocumentExtractionResult Success(DocumentExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        return new GetDocumentExtractionResult { Found = true, Extraction = extraction };
    }

    /// <summary>The extraction was not found (or inaccessible due to tenant/project scoping).</summary>
    public static GetDocumentExtractionResult NotFound() =>
        new() { Found = false };
}
