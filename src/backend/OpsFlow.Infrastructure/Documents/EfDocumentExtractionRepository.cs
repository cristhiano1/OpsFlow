using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// EF Core implementation of <see cref="IDocumentExtractionRepository"/>.
/// All reads enforce tenant isolation via a join to the Documents table.
/// Duplicate-key handling in <see cref="AddIfAbsentAsync"/> keeps SQL
/// provider details out of the Application layer.
/// </summary>
public sealed class EfDocumentExtractionRepository : IDocumentExtractionRepository
{
    private readonly OpsFlowDbContext _db;

    /// <summary>Creates the repository with the supplied database context.</summary>
    public EfDocumentExtractionRepository(OpsFlowDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<DocumentExtraction?> GetByDocumentAsync(
        Guid documentId,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return await _db.DocumentExtractions
            .Join(
                _db.Documents,
                e => e.DocumentId,
                d => d.Id,
                (e, d) => new { Extraction = e, Document = d })
            .Where(x => x.Extraction.DocumentId == documentId
                     && x.Document.ProjectId == projectId
                     && x.Document.OrganizationId == organizationId)
            .Select(x => x.Extraction)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DocumentExtractionAddResult> AddIfAbsentAsync(
        DocumentExtraction extraction,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extraction);

        try
        {
            _ = _db.DocumentExtractions.Add(extraction);
            _ = await _db.SaveChangesAsync(cancellationToken);
            return DocumentExtractionAddResult.Added(extraction);
        }
        catch (DbUpdateException)
        {
            _db.Entry(extraction).State = EntityState.Detached;

            var existing = await GetByDocumentAsync(
                extraction.DocumentId, projectId, organizationId, cancellationToken);

            if (existing is null)
            {
                throw;
            }

            return DocumentExtractionAddResult.AlreadyExists(existing);
        }
    }
}
