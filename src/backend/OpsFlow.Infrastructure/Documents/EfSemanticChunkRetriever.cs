using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// EF Core implementation of <see cref="ISemanticChunkRetriever"/>.
/// Executes a single server-side SQL query using <c>VECTOR_DISTANCE</c>.
/// </summary>
public sealed class EfSemanticChunkRetriever : ISemanticChunkRetriever
{
    private readonly OpsFlowDbContext _db;

    /// <summary>Creates the retriever with the supplied database context.</summary>
    public EfSemanticChunkRetriever(OpsFlowDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SemanticChunkHit>> RetrieveAsync(
        Guid organizationId,
        Guid projectId,
        EmbeddingGeneratorIdentity embeddingIdentity,
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embeddingIdentity);

        var queryVector = new SqlVector<float>(queryEmbedding.ToArray());

        return await _db.Set<DocumentChunkEmbeddingRow>()
            .Join(
                _db.DocumentEmbeddingSets,
                r => r.EmbeddingSetId,
                es => es.Id,
                (r, es) => new { Row = r, Set = es })
            .Join(
                _db.DocumentChunks,
                x => x.Row.DocumentChunkId,
                c => c.Id,
                (x, c) => new { x.Row, x.Set, Chunk = c })
            .Join(
                _db.Documents,
                x => x.Chunk.DocumentId,
                d => d.Id,
                (x, d) => new { x.Row, x.Set, x.Chunk, Document = d })
            .Where(x => x.Document.OrganizationId == organizationId
                     && x.Document.ProjectId == projectId
                     && x.Set.DocumentId == x.Chunk.DocumentId
                     && x.Set.ProfileId == embeddingIdentity.ProfileId
                     && x.Set.ModelId == embeddingIdentity.ModelId
                     && x.Set.Dimensions == embeddingIdentity.Dimensions)
            .Select(x => new
            {
                x.Chunk.DocumentId,
                DocumentChunkId = x.Chunk.Id,
                x.Chunk.ChunkIndex,
                x.Chunk.StartOffset,
                x.Chunk.EndOffset,
                x.Chunk.Text,
                CosineDistance = EF.Functions.VectorDistance("cosine", x.Row.Embedding, queryVector),
            })
            .OrderBy(x => x.CosineDistance)
            .ThenBy(x => x.DocumentId)
            .ThenBy(x => x.ChunkIndex)
            .Take(topK)
            .Select(x => new SemanticChunkHit(
                x.DocumentId,
                x.DocumentChunkId,
                x.ChunkIndex,
                x.StartOffset,
                x.EndOffset,
                x.Text,
                x.CosineDistance))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }
}
