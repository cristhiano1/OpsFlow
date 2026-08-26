using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// EF Core implementation of <see cref="IDocumentChunkSetRepository"/>.
/// All reads enforce tenant isolation via a join to the Documents table.
/// Duplicate-key handling in <see cref="AddIfAbsentAsync"/> keeps SQL
/// provider details out of the Application layer.
/// </summary>
public sealed class EfDocumentChunkSetRepository : IDocumentChunkSetRepository
{
    private readonly OpsFlowDbContext _db;

    /// <summary>Creates the repository with the supplied database context.</summary>
    public EfDocumentChunkSetRepository(OpsFlowDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<DocumentChunkSet?> GetByDocumentAsync(
        Guid documentId,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return await _db.DocumentChunkSets
            .Join(
                _db.Documents,
                cs => cs.DocumentId,
                d => d.Id,
                (cs, d) => new { ChunkSet = cs, Document = d })
            .Where(x => x.ChunkSet.DocumentId == documentId
                     && x.Document.ProjectId == projectId
                     && x.Document.OrganizationId == organizationId)
            .Select(x => x.ChunkSet)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DocumentChunkSetAddResult> AddIfAbsentAsync(
        DocumentChunkSet chunkSet,
        IReadOnlyList<DocumentChunk> chunks,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunkSet);
        ArgumentNullException.ThrowIfNull(chunks);

        try
        {
            _ = _db.DocumentChunkSets.Add(chunkSet);
            _db.DocumentChunks.AddRange(chunks);
            _ = await _db.SaveChangesAsync(cancellationToken);
            return DocumentChunkSetAddResult.Added(chunkSet);
        }
        catch (DbUpdateException)
        {
            _db.Entry(chunkSet).State = EntityState.Detached;
            foreach (var chunk in chunks)
            {
                _db.Entry(chunk).State = EntityState.Detached;
            }

            var existing = await GetByDocumentAsync(
                chunkSet.DocumentId, projectId, organizationId, cancellationToken);

            if (existing is null)
            {
                throw;
            }

            return DocumentChunkSetAddResult.AlreadyExists(existing);
        }
    }
}
