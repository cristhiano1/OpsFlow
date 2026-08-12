using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Projects;
using OpsFlow.Domain.Projects;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Projects;

/// <summary>
/// EF Core implementation of <see cref="IProjectRepository"/>.
/// All queries enforce organization scoping in the SQL predicate.
/// </summary>
public sealed class EfProjectRepository : IProjectRepository
{
    private readonly OpsFlowDbContext _db;

    /// <summary>Creates the repository with the supplied database context.</summary>
    public EfProjectRepository(OpsFlowDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task AddAsync(Project project, CancellationToken cancellationToken)
    {
        _ = _db.Projects.Add(project);
        _ = await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Project>> ListByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return await _db.Projects
            .Where(p => p.OrganizationId == organizationId)
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsInOrganizationAsync(
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return await _db.Projects
            .AnyAsync(p => p.Id == projectId && p.OrganizationId == organizationId, cancellationToken);
    }
}
