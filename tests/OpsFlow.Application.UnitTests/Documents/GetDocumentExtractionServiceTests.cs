using OpsFlow.Application.Documents;
using OpsFlow.Application.UnitTests.TestSupport;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class GetDocumentExtractionServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid DocumentId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

    private static (GetDocumentExtractionService Service, FakeDocumentExtractionRepository Repository)
        CreateService()
    {
        var repo = new FakeDocumentExtractionRepository();
        var service = new GetDocumentExtractionService(repo);
        return (service, repo);
    }

    [Fact]
    public void Constructor_rejects_null_repository()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GetDocumentExtractionService(null!));
    }

    [Fact]
    public async Task Returns_success_when_extraction_exists()
    {
        var (service, repo) = CreateService();
        var extraction = new DocumentExtraction(DocumentId, "Hello", Now);
        repo.GetByDocumentResult = extraction;

        var result = await service.GetAsync(
            new GetDocumentExtractionQuery(OrgId, ProjectId, DocumentId),
            CancellationToken.None);

        Assert.True(result.Found);
        Assert.Same(extraction, result.Extraction);
    }

    [Fact]
    public async Task Returns_not_found_when_extraction_does_not_exist()
    {
        var (service, _) = CreateService();

        var result = await service.GetAsync(
            new GetDocumentExtractionQuery(OrgId, ProjectId, DocumentId),
            CancellationToken.None);

        Assert.False(result.Found);
    }

    [Fact]
    public async Task Throws_for_empty_organization_id()
    {
        var (service, _) = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetAsync(
                new GetDocumentExtractionQuery(Guid.Empty, ProjectId, DocumentId),
                CancellationToken.None));
    }

    [Fact]
    public async Task Returns_not_found_for_empty_project_id()
    {
        var (service, _) = CreateService();

        var result = await service.GetAsync(
            new GetDocumentExtractionQuery(OrgId, Guid.Empty, DocumentId),
            CancellationToken.None);

        Assert.False(result.Found);
    }

    [Fact]
    public async Task Returns_not_found_for_empty_document_id()
    {
        var (service, _) = CreateService();

        var result = await service.GetAsync(
            new GetDocumentExtractionQuery(OrgId, ProjectId, Guid.Empty),
            CancellationToken.None);

        Assert.False(result.Found);
    }

    [Fact]
    public async Task Passes_correct_ids_to_repository()
    {
        var (service, repo) = CreateService();

        _ = await service.GetAsync(
            new GetDocumentExtractionQuery(OrgId, ProjectId, DocumentId),
            CancellationToken.None);

        Assert.Equal(DocumentId, repo.ReceivedGetDocumentId);
        Assert.Equal(ProjectId, repo.ReceivedGetProjectId);
        Assert.Equal(OrgId, repo.ReceivedGetOrganizationId);
    }
}
