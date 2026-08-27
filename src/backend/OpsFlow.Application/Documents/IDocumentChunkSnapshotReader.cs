namespace OpsFlow.Application.Documents;

/// <summary>
/// Read-only port for loading a document's chunk snapshot. Tenant isolation
/// is enforced via a join to the Documents table in the infrastructure
/// implementation.
/// </summary>
public interface IDocumentChunkSnapshotReader
{
    /// <summary>
    /// Returns the chunk snapshot for the specified document, scoped to the
    /// given project and organization. Returns <c>null</c> when no chunk set
    /// exists or the tenant scope does not match.
    /// </summary>
    Task<DocumentChunkSnapshot?> GetByDocumentAsync(
        Guid documentId,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken);
}
