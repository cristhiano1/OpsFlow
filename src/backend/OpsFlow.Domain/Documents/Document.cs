namespace OpsFlow.Domain.Documents;

/// <summary>
/// Metadata record for a document uploaded to a project. Belongs to a specific
/// organization and project. Does not contain file bytes or storage details —
/// those concerns belong to a future storage layer (PR #12).
/// </summary>
public class Document
{
    /// <summary>Maximum length for the original file name.</summary>
    public const int OriginalFileNameMaxLength = 255;

    /// <summary>Maximum length for the MIME content type.</summary>
    public const int ContentTypeMaxLength = 255;

    /// <summary>Database identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>Identifier of the organization this document belongs to.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Identifier of the project this document is attached to.</summary>
    public Guid ProjectId { get; private set; }

    /// <summary>The file name as supplied by the uploader.</summary>
    public string OriginalFileName { get; private set; } = string.Empty;

    /// <summary>MIME content type (e.g. <c>application/pdf</c>).</summary>
    public string ContentType { get; private set; } = string.Empty;

    /// <summary>File size in bytes. Zero is valid at the domain level.</summary>
    public long SizeBytes { get; private set; }

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    // EF Core requires a parameterless constructor.
    private Document() { }

    /// <summary>Creates a new document metadata record with validated invariants.</summary>
    public Document(
        Guid id,
        Guid organizationId,
        Guid projectId,
        string originalFileName,
        string contentType,
        long sizeBytes,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Document ID must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization ID must not be empty.", nameof(organizationId));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project ID must not be empty.", nameof(projectId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);

        if (originalFileName.Length > OriginalFileNameMaxLength)
        {
            throw new ArgumentException(
                $"OriginalFileName must not exceed {OriginalFileNameMaxLength} characters.",
                nameof(originalFileName));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        if (contentType.Length > ContentTypeMaxLength)
        {
            throw new ArgumentException(
                $"ContentType must not exceed {ContentTypeMaxLength} characters.",
                nameof(contentType));
        }

        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "SizeBytes must be >= 0.");
        }

        Id = id;
        OrganizationId = organizationId;
        ProjectId = projectId;
        OriginalFileName = originalFileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        CreatedAt = createdAt;
    }
}
