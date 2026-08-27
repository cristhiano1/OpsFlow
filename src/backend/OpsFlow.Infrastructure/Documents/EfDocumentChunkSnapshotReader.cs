using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// EF Core implementation of <see cref="IDocumentChunkSnapshotReader"/>.
/// Uses two queries: a tenant-scoped chunk set lookup, then ordered chunk
/// projection. No navigation properties exist, so Include is not used.
/// </summary>
public sealed class EfDocumentChunkSnapshotReader : IDocumentChunkSnapshotReader
{
    private readonly OpsFlowDbContext _db;

    /// <summary>Creates the reader with the supplied database context.</summary>
    public EfDocumentChunkSnapshotReader(OpsFlowDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<DocumentChunkSnapshot?> GetByDocumentAsync(
        Guid documentId,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var chunkSetInfo = await _db.DocumentChunkSets
            .Join(
                _db.Documents,
                cs => cs.DocumentId,
                d => d.Id,
                (cs, d) => new { ChunkSet = cs, Document = d })
            .Where(x => x.ChunkSet.DocumentId == documentId
                     && x.Document.ProjectId == projectId
                     && x.Document.OrganizationId == organizationId)
            .Select(x => new { x.ChunkSet.DocumentId, x.ChunkSet.ChunkingVersion, x.ChunkSet.ChunkCount })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (chunkSetInfo is null)
        {
            return null;
        }

        var chunks = await _db.DocumentChunks
            .Where(c => c.DocumentId == documentId)
            .OrderBy(c => c.ChunkIndex)
            .Select(c => new DocumentChunkSource(c.Id, c.ChunkIndex, c.Text))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new DocumentChunkSnapshot(
            chunkSetInfo.DocumentId,
            chunkSetInfo.ChunkingVersion,
            chunkSetInfo.ChunkCount,
            chunks);
    }
}
