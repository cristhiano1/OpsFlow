using OpsFlow.Domain.Projects;

namespace OpsFlow.Application.Projects;

/// <summary>
/// Coordinates the list-projects use case. Returns all projects belonging to
/// the authenticated caller's organization in deterministic order.
/// </summary>
public sealed class ListProjectsService
{
    private readonly IProjectRepository _repository;

    /// <summary>Creates the service with its repository dependency.</summary>
    public ListProjectsService(IProjectRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    /// <summary>
    /// Returns all projects for the specified organization in deterministic
    /// order (<c>CreatedAt DESC</c>, <c>Id DESC</c>).
    /// </summary>
    public async Task<IReadOnlyList<Project>> ListAsync(
        ListProjectsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization ID must not be empty.", nameof(query));
        }

        return await _repository.ListByOrganizationAsync(query.OrganizationId, cancellationToken);
    }
}
