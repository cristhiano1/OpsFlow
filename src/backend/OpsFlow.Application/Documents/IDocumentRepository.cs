using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Persistence port for document operations. The infrastructure layer provides
/// the EF Core implementation. All queries enforce both project and organization
/// scoping for defence-in-depth tenant isolation.
/// </summary>
public interface IDocumentRepository
{
    /// <summary>
    /// Returns all documents belonging to the specified project within the
    /// specified organization, ordered by <c>CreatedAt</c> descending then
    /// <c>Id</c> descending. Both predicates are evaluated in SQL.
    /// </summary>
    Task<IReadOnlyList<Document>> ListByProjectAsync(
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the document matching all three scoping predicates, or <c>null</c>
    /// when no such record exists. Cross-tenant documents are indistinguishable
    /// from nonexistent ones.
    /// </summary>
    Task<Document?> GetByProjectAsync(
        Guid documentId,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken);

    /// <summary>Persists a new document metadata record.</summary>
    Task AddAsync(Document document, CancellationToken cancellationToken);
}
