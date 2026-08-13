using OpsFlow.Application.Documents;
using OpsFlow.Application.UnitTests.TestSupport;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class GetDocumentContentServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid DocumentId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

    private static (GetDocumentContentService Service, FakeDocumentRepository Documents, FakeDocumentStorage Storage)
        CreateService(Document? documentResult = null)
    {
        var documents = new FakeDocumentRepository { GetByProjectResult = documentResult };
        var storage = new FakeDocumentStorage();
        var service = new GetDocumentContentService(documents, storage);
        return (service, documents, storage);
    }

    private static Document MakeDocument(
        Guid? id = null, Guid? orgId = null, Guid? projectId = null) =>
        new(
            id ?? DocumentId,
            orgId ?? OrgId,
            projectId ?? ProjectId,
            "report.pdf",
            "application/pdf",
            4096,
            Now);

    // ================================================================
    // Constructor null guards
    // ================================================================

    [Fact]
    public void Constructor_rejects_null_document_repository()
    {
        _ = Assert.Throws<ArgumentNullException>(() =>
            new GetDocumentContentService(null!, new FakeDocumentStorage()));
    }

    [Fact]
    public void Constructor_rejects_null_document_storage()
    {
        _ = Assert.Throws<ArgumentNullException>(() =>
            new GetDocumentContentService(new FakeDocumentRepository(), null!));
    }

    // ================================================================
    // Query validation
    // ================================================================

    [Fact]
    public async Task Null_query_is_rejected()
    {
        var (service, _, _) = CreateService();
        _ = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.GetAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Empty_organization_id_throws()
    {
        var (service, _, _) = CreateService();
        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetAsync(
                new GetDocumentContentQuery(Guid.Empty, ProjectId, DocumentId),
                CancellationToken.None));
    }

    [Fact]
    public async Task Empty_project_id_returns_NotFound()
    {
        var (service, _, _) = CreateService();
        var result = await service.GetAsync(
            new GetDocumentContentQuery(OrgId, Guid.Empty, DocumentId),
            CancellationToken.None);
        Assert.Equal(GetDocumentContentStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Empty_document_id_returns_NotFound()
    {
        var (service, _, _) = CreateService();
        var result = await service.GetAsync(
            new GetDocumentContentQuery(OrgId, ProjectId, Guid.Empty),
            CancellationToken.None);
        Assert.Equal(GetDocumentContentStatus.NotFound, result.Status);
    }

    // ================================================================
    // Document not found
    // ================================================================

    [Fact]
    public async Task Absent_document_returns_NotFound()
    {
        var (service, _, _) = CreateService(documentResult: null);
        var result = await service.GetAsync(
            new GetDocumentContentQuery(OrgId, ProjectId, DocumentId),
            CancellationToken.None);
        Assert.Equal(GetDocumentContentStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Wrong_project_returns_NotFound()
    {
        var (service, _, _) = CreateService(documentResult: null);
        var result = await service.GetAsync(
            new GetDocumentContentQuery(OrgId, Guid.NewGuid(), DocumentId),
            CancellationToken.None);
        Assert.Equal(GetDocumentContentStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Wrong_tenant_returns_NotFound()
    {
        var (service, _, _) = CreateService(documentResult: null);
        var result = await service.GetAsync(
            new GetDocumentContentQuery(Guid.NewGuid(), ProjectId, DocumentId),
            CancellationToken.None);
        Assert.Equal(GetDocumentContentStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Storage_not_opened_when_metadata_lookup_fails()
    {
        var (service, _, storage) = CreateService(documentResult: null);
        _ = await service.GetAsync(
            new GetDocumentContentQuery(OrgId, ProjectId, DocumentId),
            CancellationToken.None);
        Assert.False(storage.OpenReadCalled);
    }

    // ================================================================
    // Repository receives correct IDs
    // ================================================================

    [Fact]
    public async Task Repository_receives_all_three_ids()
    {
        var doc = MakeDocument();
        var (service, documents, storage) = CreateService(doc);
        storage.OpenReadResult = new MemoryStream([0x01]);

        _ = await service.GetAsync(
            new GetDocumentContentQuery(OrgId, ProjectId, DocumentId),
            CancellationToken.None);

        Assert.Equal(DocumentId, documents.ReceivedGetDocumentId);
        Assert.Equal(ProjectId, documents.ReceivedGetProjectId);
        Assert.Equal(OrgId, documents.ReceivedGetOrganizationId);
    }

    // ================================================================
    // Successful download
    // ================================================================

    [Fact]
    public async Task Success_returns_metadata_and_stream()
    {
        var doc = MakeDocument();
        var contentStream = new MemoryStream([0xCA, 0xFE]);
        var (service, _, storage) = CreateService(doc);
        storage.OpenReadResult = contentStream;

        var result = await service.GetAsync(
            new GetDocumentContentQuery(OrgId, ProjectId, DocumentId),
            CancellationToken.None);

        Assert.Equal(GetDocumentContentStatus.Success, result.Status);
        Assert.Same(doc, result.Metadata);
        Assert.Same(contentStream, result.Content);
    }

    [Fact]
    public async Task Correct_storage_address_constructed()
    {
        var doc = MakeDocument();
        var (service, _, storage) = CreateService(doc);
        storage.OpenReadResult = new MemoryStream([0x01]);

        _ = await service.GetAsync(
            new GetDocumentContentQuery(OrgId, ProjectId, DocumentId),
            CancellationToken.None);

        var addr = storage.LastOpenReadAddress;
        Assert.NotNull(addr);
        Assert.Equal(OrgId, addr.OrganizationId);
        Assert.Equal(ProjectId, addr.ProjectId);
        Assert.Equal(DocumentId, addr.DocumentId);
    }

    // ================================================================
    // Storage missing
    // ================================================================

    [Fact]
    public async Task Storage_missing_returns_StorageMissing()
    {
        var doc = MakeDocument();
        var (service, _, storage) = CreateService(doc);
        storage.OpenReadResult = null;

        var result = await service.GetAsync(
            new GetDocumentContentQuery(OrgId, ProjectId, DocumentId),
            CancellationToken.None);

        Assert.Equal(GetDocumentContentStatus.StorageMissing, result.Status);
    }

    // ================================================================
    // Storage exception propagation
    // ================================================================

    [Fact]
    public async Task Storage_exception_propagates()
    {
        var doc = MakeDocument();
        var (service, _, storage) = CreateService(doc);
        storage.OpenReadException = new IOException("Disk failure");

        _ = await Assert.ThrowsAsync<IOException>(() =>
            service.GetAsync(
                new GetDocumentContentQuery(OrgId, ProjectId, DocumentId),
                CancellationToken.None));
    }

    // ================================================================
    // Cancellation
    // ================================================================

    [Fact]
    public async Task Cancellation_before_storage_propagates()
    {
        var doc = MakeDocument();
        var (service, _, _) = CreateService(doc);
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetAsync(
                new GetDocumentContentQuery(OrgId, ProjectId, DocumentId),
                cts.Token));
    }
}
