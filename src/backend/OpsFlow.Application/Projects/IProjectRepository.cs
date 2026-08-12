using OpsFlow.Domain.Projects;

namespace OpsFlow.Application.Projects;

/// <summary>
/// Persistence port for project operations. The infrastructure layer provides
/// the EF Core implementation. Methods enforce organization scoping.
/// </summary>
public interface IProjectRepository
{
    /// <summary>Persists a new project.</summary>
    Task AddAsync(Project project, CancellationToken cancellationToken);

    /// <summary>
    /// Returns all projects belonging to the specified organization, ordered
    /// by <c>CreatedAt</c> descending then <c>Id</c> descending.
    /// </summary>
    Task<IReadOnlyList<Project>> ListByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns <see langword="true"/> if a project with the given <paramref name="projectId"/>
    /// exists and belongs to the given <paramref name="organizationId"/>; otherwise
    /// <see langword="false"/>. Both predicates are evaluated in SQL.
    /// </summary>
    Task<bool> ExistsInOrganizationAsync(Guid projectId, Guid organizationId, CancellationToken cancellationToken);
}
