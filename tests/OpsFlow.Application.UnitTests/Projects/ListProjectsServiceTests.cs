using OpsFlow.Application.Projects;
using OpsFlow.Application.UnitTests.TestSupport;
using OpsFlow.Domain.Projects;

namespace OpsFlow.Application.UnitTests.Projects;

public sealed class ListProjectsServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();

    [Fact]
    public async Task Organization_id_is_passed_to_repository()
    {
        var repo = new FakeProjectRepository { ListResult = [] };
        var service = new ListProjectsService(repo);

        _ = await service.ListAsync(new ListProjectsQuery(OrgId), CancellationToken.None);

        Assert.Equal(OrgId, repo.ReceivedOrganizationId);
    }

    [Fact]
    public async Task Results_are_returned_from_repository()
    {
        var project = new Project(Guid.NewGuid(), OrgId, "P1", null,
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));

        var repo = new FakeProjectRepository { ListResult = [project] };
        var service = new ListProjectsService(repo);

        var result = await service.ListAsync(new ListProjectsQuery(OrgId), CancellationToken.None);

        _ = Assert.Single(result);
        Assert.Equal("P1", result[0].Name);
    }

    [Fact]
    public async Task Empty_organization_id_throws()
    {
        var repo = new FakeProjectRepository { ListResult = [] };
        var service = new ListProjectsService(repo);

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ListAsync(new ListProjectsQuery(Guid.Empty), CancellationToken.None));
    }
}
