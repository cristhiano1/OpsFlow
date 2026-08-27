using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Persistence port for document embedding sets. All operations enforce
/// tenant isolation via a join to the Documents table.
/// </summary>
public interface IDocumentEmbeddingSetRepository
{
    /// <summary>
    /// Returns the embedding set for the specified document and profile,
    /// scoped to the given project and organization. Returns <c>null</c>
    /// when no embedding set exists or the tenant scope does not match.
    /// </summary>
    Task<DocumentEmbeddingSet?> GetByDocumentAndProfileAsync(
        Guid documentId,
        string profileId,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically inserts the embedding set and all its embedding rows in a
    /// single transaction. Verifies tenant ownership before inserting.
    /// On a concurrent duplicate-key conflict for (DocumentId, ProfileId),
    /// re-reads the existing set (tenant-scoped) and returns
    /// <see cref="DocumentEmbeddingSetAddResult.AlreadyExists"/>.
    /// Returns <see cref="DocumentEmbeddingSetAddResult.NotFound"/> if the
    /// document cannot be verified within the tenant scope.
    /// </summary>
    Task<DocumentEmbeddingSetAddResult> AddIfAbsentAsync(
        DocumentEmbeddingSet embeddingSet,
        IReadOnlyList<ChunkEmbeddingInput> embeddings,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken);
}
