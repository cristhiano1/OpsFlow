using OpsFlow.Application.Projects;
using OpsFlow.Domain.Projects;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeProjectRepository : IProjectRepository
{
    public List<Project> Added { get; } = [];

    public IReadOnlyList<Project>? ListResult { get; set; }

    public Guid? ReceivedOrganizationId { get; private set; }

    public Task AddAsync(Project project, CancellationToken cancellationToken)
    {
        Added.Add(project);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Project>> ListByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        ReceivedOrganizationId = organizationId;
        IReadOnlyList<Project> result = ListResult ?? [];
        return Task.FromResult(result);
    }
}
