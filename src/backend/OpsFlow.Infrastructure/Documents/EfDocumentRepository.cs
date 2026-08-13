using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// EF Core implementation of <see cref="IDocumentRepository"/>.
/// All queries enforce both project and organization scoping in the SQL predicate.
/// </summary>
public sealed class EfDocumentRepository : IDocumentRepository
{
    private readonly OpsFlowDbContext _db;

    /// <summary>Creates the repository with the supplied database context.</summary>
    public EfDocumentRepository(OpsFlowDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task AddAsync(Document document, CancellationToken cancellationToken)
    {
        _ = _db.Documents.Add(document);
        _ = await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Document?> GetByProjectAsync(
        Guid documentId,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return await _db.Documents
            .Where(d => d.Id == documentId
                     && d.ProjectId == projectId
                     && d.OrganizationId == organizationId)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Document>> ListByProjectAsync(
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return await _db.Documents
            .Where(d => d.ProjectId == projectId && d.OrganizationId == organizationId)
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }
}
