using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Persistence port for document extractions. All reads enforce tenant
/// isolation via a join to the Documents table. The infrastructure layer
/// encapsulates duplicate-key handling in <see cref="AddIfAbsentAsync"/>.
/// </summary>
public interface IDocumentExtractionRepository
{
    /// <summary>
    /// Returns the extraction for the specified document, scoped to the given
    /// project and organization via a join to Documents. Returns <c>null</c>
    /// when no extraction exists or the tenant scope does not match.
    /// </summary>
    Task<DocumentExtraction?> GetByDocumentAsync(
        Guid documentId,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inserts the extraction if no row for the document exists. On a
    /// concurrent duplicate-key conflict, re-reads the existing row (tenant-
    /// scoped) and returns <see cref="DocumentExtractionAddResult.AlreadyExists"/>.
    /// </summary>
    Task<DocumentExtractionAddResult> AddIfAbsentAsync(
        DocumentExtraction extraction,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken);
}
