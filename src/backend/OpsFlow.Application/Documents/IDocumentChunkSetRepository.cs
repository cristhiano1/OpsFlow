using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Persistence port for document chunk sets. All reads enforce tenant
/// isolation via a join to the Documents table. The infrastructure layer
/// encapsulates duplicate-key handling in <see cref="AddIfAbsentAsync"/>.
/// </summary>
public interface IDocumentChunkSetRepository
{
    /// <summary>
    /// Returns the chunk set for the specified document, scoped to the given
    /// project and organization via a join to Documents. Returns <c>null</c>
    /// when no chunk set exists or the tenant scope does not match.
    /// </summary>
    Task<DocumentChunkSet?> GetByDocumentAsync(
        Guid documentId,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically inserts the chunk set and all its chunks in a single
    /// transaction. On a concurrent duplicate-key conflict, re-reads the
    /// existing chunk set (tenant-scoped) and returns
    /// <see cref="DocumentChunkSetAddResult.AlreadyExists"/>.
    /// </summary>
    Task<DocumentChunkSetAddResult> AddIfAbsentAsync(
        DocumentChunkSet chunkSet,
        IReadOnlyList<DocumentChunk> chunks,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken);
}
