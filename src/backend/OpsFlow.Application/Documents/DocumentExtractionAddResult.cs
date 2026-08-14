using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of an idempotent extraction insert. Encapsulates the duplicate-key
/// resolution so that Application code never references EF Core or SQL
/// provider details.
/// </summary>
public sealed class DocumentExtractionAddResult
{
    /// <summary>Whether the extraction was newly inserted.</summary>
    public bool WasAdded { get; private set; }

    /// <summary>The extraction entity (newly inserted or the pre-existing row).</summary>
    public DocumentExtraction Extraction { get; private set; } = null!;

    private DocumentExtractionAddResult() { }

    /// <summary>The extraction was newly inserted.</summary>
    public static DocumentExtractionAddResult Added(DocumentExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        return new DocumentExtractionAddResult { WasAdded = true, Extraction = extraction };
    }

    /// <summary>A row for this document already existed.</summary>
    public static DocumentExtractionAddResult AlreadyExists(DocumentExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        return new DocumentExtractionAddResult { WasAdded = false, Extraction = extraction };
    }
}
