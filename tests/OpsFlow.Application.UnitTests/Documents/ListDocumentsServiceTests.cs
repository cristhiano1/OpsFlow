using OpsFlow.Application.Documents;
using OpsFlow.Application.UnitTests.TestSupport;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class ListDocumentsServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static (ListDocumentsService Service, FakeProjectRepository Projects, FakeDocumentRepository Documents)
        CreateService(bool projectExists = true)
    {
        var projects = new FakeProjectRepository { ExistsResult = projectExists };
        var documents = new FakeDocumentRepository();
        var service = new ListDocumentsService(projects, documents);
        return (service, projects, documents);
    }

    [Fact]
    public void Constructor_rejects_null_project_repository()
    {
        _ = Assert.Throws<ArgumentNullException>(() =>
            new ListDocumentsService(null!, new FakeDocumentRepository()));
    }

    [Fact]
    public void Constructor_rejects_null_document_repository()
    {
        _ = Assert.Throws<ArgumentNullException>(() =>
            new ListDocumentsService(new FakeProjectRepository(), null!));
    }

    [Fact]
    public async Task Null_query_is_rejected()
    {
        var (service, _, _) = CreateService();

        _ = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.ListAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Empty_organization_id_throws()
    {
        var (service, _, _) = CreateService();

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ListAsync(new ListDocumentsQuery(Guid.Empty, ProjectId), CancellationToken.None));
    }

    [Fact]
    public async Task Empty_project_id_returns_ProjectNotFound()
    {
        var (service, _, _) = CreateService();

        var result = await service.ListAsync(
            new ListDocumentsQuery(OrgId, Guid.Empty), CancellationToken.None);

        Assert.False(result.ProjectFound);
    }

    [Fact]
    public async Task Nonexistent_project_returns_ProjectNotFound()
    {
        var (service, _, _) = CreateService(projectExists: false);

        var result = await service.ListAsync(
            new ListDocumentsQuery(OrgId, ProjectId), CancellationToken.None);

        Assert.False(result.ProjectFound);
    }

    [Fact]
    public async Task Document_repository_is_not_queried_when_project_not_found()
    {
        var (service, _, docs) = CreateService(projectExists: false);

        _ = await service.ListAsync(
            new ListDocumentsQuery(OrgId, ProjectId), CancellationToken.None);

        Assert.Null(docs.ReceivedProjectId);
    }

    [Fact]
    public async Task Document_repository_is_not_queried_when_project_id_is_empty()
    {
        var (service, _, docs) = CreateService();

        _ = await service.ListAsync(
            new ListDocumentsQuery(OrgId, Guid.Empty), CancellationToken.None);

        Assert.Null(docs.ReceivedProjectId);
    }

    [Fact]
    public async Task Project_existence_lookup_receives_both_ids()
    {
        var (service, projects, _) = CreateService(projectExists: true);

        _ = await service.ListAsync(
            new ListDocumentsQuery(OrgId, ProjectId), CancellationToken.None);

        Assert.Equal(ProjectId, projects.ReceivedExistsProjectId);
        Assert.Equal(OrgId, projects.ReceivedExistsOrganizationId);
    }

    [Fact]
    public async Task Document_repository_receives_both_ids()
    {
        var (service, _, docs) = CreateService(projectExists: true);

        _ = await service.ListAsync(
            new ListDocumentsQuery(OrgId, ProjectId), CancellationToken.None);

        Assert.Equal(ProjectId, docs.ReceivedProjectId);
        Assert.Equal(OrgId, docs.ReceivedOrganizationId);
    }

    [Fact]
    public async Task Existing_project_returns_Success()
    {
        var (service, _, _) = CreateService(projectExists: true);

        var result = await service.ListAsync(
            new ListDocumentsQuery(OrgId, ProjectId), CancellationToken.None);

        Assert.True(result.ProjectFound);
    }

    [Fact]
    public async Task Documents_returned_by_repository_are_returned_by_service()
    {
        var now = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
        var document = new Document(
            Guid.NewGuid(), OrgId, ProjectId, "report.pdf", "application/pdf", 1024, now);

        var (service, _, docs) = CreateService(projectExists: true);
        docs.ListResult = [document];

        var result = await service.ListAsync(
            new ListDocumentsQuery(OrgId, ProjectId), CancellationToken.None);

        Assert.True(result.ProjectFound);
        _ = Assert.Single(result.Documents);
        Assert.Equal("report.pdf", result.Documents[0].OriginalFileName);
    }
}
