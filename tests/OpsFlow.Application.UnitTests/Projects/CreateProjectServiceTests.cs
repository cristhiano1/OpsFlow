using OpsFlow.Application.Projects;
using OpsFlow.Application.UnitTests.TestSupport;
using OpsFlow.Domain.Projects;

namespace OpsFlow.Application.UnitTests.Projects;

public sealed class CreateProjectServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 10, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrgId = Guid.NewGuid();

    private static (CreateProjectService Service, FakeProjectRepository Repo) CreateService()
    {
        var repo = new FakeProjectRepository();
        var clock = new FixedClock(FixedNow);
        var service = new CreateProjectService(repo, clock);
        return (service, repo);
    }

    [Fact]
    public async Task Valid_project_is_persisted()
    {
        var (service, repo) = CreateService();

        var result = await service.CreateAsync(
            new CreateProjectCommand(OrgId, "My Project", "A description"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Project);
        _ = Assert.Single(repo.Added);
        Assert.Equal("My Project", repo.Added[0].Name);
    }

    [Fact]
    public async Task Organization_id_is_propagated()
    {
        var (service, repo) = CreateService();

        var result = await service.CreateAsync(
            new CreateProjectCommand(OrgId, "Name", null),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(OrgId, repo.Added[0].OrganizationId);
    }

    [Fact]
    public async Task Name_is_trimmed()
    {
        var (service, repo) = CreateService();

        _ = await service.CreateAsync(
            new CreateProjectCommand(OrgId, "  Trimmed Name  ", null),
            CancellationToken.None);

        Assert.Equal("Trimmed Name", repo.Added[0].Name);
    }

    [Fact]
    public async Task Description_is_trimmed()
    {
        var (service, repo) = CreateService();

        _ = await service.CreateAsync(
            new CreateProjectCommand(OrgId, "Name", "  Trimmed Desc  "),
            CancellationToken.None);

        Assert.Equal("Trimmed Desc", repo.Added[0].Description);
    }

    [Fact]
    public async Task Whitespace_description_becomes_null()
    {
        var (service, repo) = CreateService();

        _ = await service.CreateAsync(
            new CreateProjectCommand(OrgId, "Name", "   "),
            CancellationToken.None);

        Assert.Null(repo.Added[0].Description);
    }

    [Fact]
    public async Task IClock_value_becomes_CreatedAt()
    {
        var (service, repo) = CreateService();

        _ = await service.CreateAsync(
            new CreateProjectCommand(OrgId, "Name", null),
            CancellationToken.None);

        Assert.Equal(FixedNow, repo.Added[0].CreatedAt);
    }

    [Fact]
    public async Task Null_name_returns_validation_error()
    {
        var (service, repo) = CreateService();

        var result = await service.CreateAsync(
            new CreateProjectCommand(OrgId, null, null),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Empty(repo.Added);
    }

    [Fact]
    public async Task Empty_name_returns_validation_error()
    {
        var (service, repo) = CreateService();

        var result = await service.CreateAsync(
            new CreateProjectCommand(OrgId, "", null),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(repo.Added);
    }

    [Fact]
    public async Task Name_exceeding_max_length_returns_validation_error()
    {
        var (service, repo) = CreateService();

        var result = await service.CreateAsync(
            new CreateProjectCommand(OrgId, new string('A', Project.NameMaxLength + 1), null),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(repo.Added);
    }

    [Fact]
    public async Task Description_exceeding_max_length_returns_validation_error()
    {
        var (service, repo) = CreateService();

        var result = await service.CreateAsync(
            new CreateProjectCommand(OrgId, "Name", new string('B', Project.DescriptionMaxLength + 1)),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(repo.Added);
    }

    [Fact]
    public async Task Empty_organization_id_returns_validation_error()
    {
        var (service, repo) = CreateService();

        var result = await service.CreateAsync(
            new CreateProjectCommand(Guid.Empty, "Name", null),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(repo.Added);
    }
}
