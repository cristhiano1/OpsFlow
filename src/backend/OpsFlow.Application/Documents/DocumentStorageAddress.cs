namespace OpsFlow.Application.Documents;

/// <summary>
/// Trusted address identifying a document's storage location. Infrastructure maps
/// this to the physical storage path. All IDs are server-generated and validated
/// non-empty.
/// </summary>
public sealed record DocumentStorageAddress
{
    /// <summary>The organization that owns the document.</summary>
    public Guid OrganizationId { get; }

    /// <summary>The project the document belongs to.</summary>
    public Guid ProjectId { get; }

    /// <summary>The unique document identifier.</summary>
    public Guid DocumentId { get; }

    /// <summary>Creates a validated storage address.</summary>
    public DocumentStorageAddress(Guid organizationId, Guid projectId, Guid documentId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization ID must not be empty.", nameof(organizationId));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project ID must not be empty.", nameof(projectId));
        }

        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Document ID must not be empty.", nameof(documentId));
        }

        OrganizationId = organizationId;
        ProjectId = projectId;
        DocumentId = documentId;
    }
}
