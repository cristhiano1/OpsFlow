namespace OpsFlow.Domain.Documents;

/// <summary>
/// Immutable record of a successful text extraction from a <see cref="Document"/>.
/// One extraction per document; <see cref="DocumentId"/> is the primary key.
/// </summary>
public sealed class DocumentExtraction
{
    /// <summary>The document this extraction belongs to.</summary>
    public Guid DocumentId { get; private set; }

    /// <summary>The normalized extracted text (may be empty for image-only documents).</summary>
    public string ExtractedText { get; private set; } = string.Empty;

    /// <summary>UTC timestamp when the extraction was performed.</summary>
    public DateTimeOffset ExtractedAt { get; private set; }

    private DocumentExtraction() { }

    /// <summary>Creates a new extraction with validated invariants.</summary>
    public DocumentExtraction(Guid documentId, string extractedText, DateTimeOffset extractedAt)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Document ID must not be empty.", nameof(documentId));
        }

        ArgumentNullException.ThrowIfNull(extractedText);

        DocumentId = documentId;
        ExtractedText = extractedText;
        ExtractedAt = extractedAt;
    }
}
