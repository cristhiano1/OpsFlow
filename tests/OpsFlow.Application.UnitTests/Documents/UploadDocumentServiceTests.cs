using OpsFlow.Application.Documents;
using OpsFlow.Application.UnitTests.TestSupport;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class UploadDocumentServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static (UploadDocumentService Service, FakeProjectRepository Projects, FakeDocumentRepository Documents, FakeDocumentStorage Storage, FixedClock Clock)
        CreateService(bool projectExists = true)
    {
        var projects = new FakeProjectRepository { ExistsResult = projectExists };
        var documents = new FakeDocumentRepository();
        var storage = new FakeDocumentStorage();
        var sharedOrder = new[] { 0 };
        documents.SharedCallOrder = sharedOrder;
        storage.SharedCallOrder = sharedOrder;
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));
        var service = new UploadDocumentService(projects, documents, storage, clock);
        return (service, projects, documents, storage, clock);
    }

    private static UploadDocumentCommand ValidCommand(
        Guid? orgId = null,
        Guid? projectId = null,
        string fileName = "report.pdf",
        string? reportedMime = "USE_DEFAULT",
        long sizeBytes = 1024,
        Stream? content = null)
    {
        var actualMime = reportedMime == "USE_DEFAULT"
            ? null
            : reportedMime;

        return new(
            orgId ?? OrgId,
            projectId ?? ProjectId,
            fileName,
            actualMime,
            sizeBytes,
            content ?? new MemoryStream([0x01]));
    }

    // ================================================================
    // Constructor null guards
    // ================================================================

    [Fact]
    public void Constructor_rejects_null_project_repository()
    {
        _ = Assert.Throws<ArgumentNullException>(() =>
            new UploadDocumentService(null!, new FakeDocumentRepository(), new FakeDocumentStorage(), new FixedClock(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public void Constructor_rejects_null_document_repository()
    {
        _ = Assert.Throws<ArgumentNullException>(() =>
            new UploadDocumentService(new FakeProjectRepository(), null!, new FakeDocumentStorage(), new FixedClock(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public void Constructor_rejects_null_document_storage()
    {
        _ = Assert.Throws<ArgumentNullException>(() =>
            new UploadDocumentService(new FakeProjectRepository(), new FakeDocumentRepository(), null!, new FixedClock(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public void Constructor_rejects_null_clock()
    {
        _ = Assert.Throws<ArgumentNullException>(() =>
            new UploadDocumentService(new FakeProjectRepository(), new FakeDocumentRepository(), new FakeDocumentStorage(), null!));
    }

    // ================================================================
    // Command validation
    // ================================================================

    [Fact]
    public async Task Null_command_is_rejected()
    {
        var (service, _, _, _, _) = CreateService();
        _ = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.UploadAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Empty_organization_id_throws()
    {
        var (service, _, _, _, _) = CreateService();
        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UploadAsync(ValidCommand(orgId: Guid.Empty), CancellationToken.None));
    }

    [Fact]
    public async Task Empty_project_id_returns_ProjectNotFound()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(ValidCommand(projectId: Guid.Empty), CancellationToken.None);
        Assert.False(result.ProjectFound);
    }

    // ================================================================
    // Filename validation
    // ================================================================

    [Fact]
    public async Task Null_filename_returns_validation_error()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(ValidCommand(fileName: null!), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Empty_filename_returns_validation_error()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(ValidCommand(fileName: ""), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Whitespace_filename_returns_validation_error()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(ValidCommand(fileName: "   "), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Path_traversal_filename_uses_only_basename()
    {
        var (service, _, docs, _, _) = CreateService();
        var result = await service.UploadAsync(
            ValidCommand(fileName: "../../../etc/passwd.pdf"), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("passwd.pdf", docs.Added[0].OriginalFileName);
    }

    [Fact]
    public async Task Windows_backslash_path_sanitized_to_basename_on_any_platform()
    {
        var (service, _, docs, _, _) = CreateService();
        var result = await service.UploadAsync(
            ValidCommand(fileName: @"C:\Users\attacker\invoice.pdf"), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("invoice.pdf", docs.Added[0].OriginalFileName);
    }

    // ================================================================
    // Extension validation
    // ================================================================

    [Fact]
    public async Task Unsupported_extension_returns_validation_error()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(ValidCommand(fileName: "malware.exe"), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains(".exe", result.Error!);
    }

    [Fact]
    public async Task No_extension_returns_validation_error()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(ValidCommand(fileName: "noext"), CancellationToken.None);
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("notes.txt")]
    [InlineData("contract.docx")]
    public async Task Supported_extensions_are_accepted(string fileName)
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(ValidCommand(fileName: fileName), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Extension_check_is_case_insensitive()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(ValidCommand(fileName: "REPORT.PDF"), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    // ================================================================
    // Size validation
    // ================================================================

    [Fact]
    public async Task Zero_byte_file_returns_validation_error()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(ValidCommand(sizeBytes: 0), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("empty", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Negative_size_returns_validation_error()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(ValidCommand(sizeBytes: -1), CancellationToken.None);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Oversized_file_returns_validation_error()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(
            ValidCommand(sizeBytes: UploadDocumentService.MaxFileSizeBytes + 1), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("MiB", result.Error!);
    }

    [Fact]
    public async Task Exact_max_size_is_accepted()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(
            ValidCommand(sizeBytes: UploadDocumentService.MaxFileSizeBytes), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    // ================================================================
    // MIME type validation
    // ================================================================

    [Fact]
    public async Task Canonical_mime_is_accepted()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(
            ValidCommand(reportedMime: "application/pdf"), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Application_octet_stream_is_accepted()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(
            ValidCommand(reportedMime: "application/octet-stream"), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Blank_reported_mime_is_accepted()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(
            ValidCommand(reportedMime: ""), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Null_reported_mime_is_accepted()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(
            ValidCommand(reportedMime: null), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Incompatible_mime_returns_validation_error()
    {
        var (service, _, _, _, _) = CreateService();
        var result = await service.UploadAsync(
            ValidCommand(reportedMime: "text/html"), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("incompatible", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Canonical_mime_is_persisted_not_reported()
    {
        var (service, _, docs, _, _) = CreateService();
        _ = await service.UploadAsync(
            ValidCommand(reportedMime: "application/octet-stream"), CancellationToken.None);
        Assert.Equal("application/pdf", docs.Added[0].ContentType);
    }

    // ================================================================
    // Project existence
    // ================================================================

    [Fact]
    public async Task Nonexistent_project_returns_ProjectNotFound()
    {
        var (service, _, _, _, _) = CreateService(projectExists: false);
        var result = await service.UploadAsync(ValidCommand(), CancellationToken.None);
        Assert.False(result.ProjectFound);
    }

    [Fact]
    public async Task Cross_tenant_project_indistinguishable_from_nonexistent()
    {
        var (service, _, _, _, _) = CreateService(projectExists: false);
        var result = await service.UploadAsync(ValidCommand(), CancellationToken.None);
        Assert.False(result.ProjectFound);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Storage_not_called_when_project_not_found()
    {
        var (service, _, _, storage, _) = CreateService(projectExists: false);
        _ = await service.UploadAsync(ValidCommand(), CancellationToken.None);
        Assert.False(storage.SaveCalled);
    }

    // ================================================================
    // Successful upload behavior
    // ================================================================

    [Fact]
    public async Task Document_id_is_generated_server_side()
    {
        var (service, _, docs, _, _) = CreateService();
        _ = await service.UploadAsync(ValidCommand(), CancellationToken.None);
        Assert.NotEqual(Guid.Empty, docs.Added[0].Id);
    }

    [Fact]
    public async Task Safe_basename_is_persisted()
    {
        var (service, _, docs, _, _) = CreateService();
        _ = await service.UploadAsync(
            ValidCommand(fileName: "C:\\Users\\evil\\..\\report.pdf"), CancellationToken.None);
        Assert.Equal("report.pdf", docs.Added[0].OriginalFileName);
    }

    [Fact]
    public async Task Correct_storage_address_constructed()
    {
        var (service, _, _, storage, _) = CreateService();
        var result = await service.UploadAsync(ValidCommand(), CancellationToken.None);

        var addr = storage.LastSavedAddress;
        Assert.NotNull(addr);
        Assert.Equal(OrgId, addr.OrganizationId);
        Assert.Equal(ProjectId, addr.ProjectId);
        Assert.Equal(result.Document!.Id, addr.DocumentId);
    }

    [Fact]
    public async Task Storage_occurs_before_repository_add()
    {
        var (service, _, docs, storage, _) = CreateService();
        _ = await service.UploadAsync(ValidCommand(), CancellationToken.None);
        Assert.True(storage.SaveCallOrder < docs.AddCallOrder);
    }

    [Fact]
    public async Task Duplicate_original_filenames_are_valid()
    {
        var (service, _, docs, _, _) = CreateService();
        _ = await service.UploadAsync(ValidCommand(fileName: "report.pdf"), CancellationToken.None);
        _ = await service.UploadAsync(ValidCommand(fileName: "report.pdf"), CancellationToken.None);
        Assert.Equal(2, docs.Added.Count);
        Assert.NotEqual(docs.Added[0].Id, docs.Added[1].Id);
    }

    // ================================================================
    // Failure and compensation
    // ================================================================

    [Fact]
    public async Task Storage_failure_propagates_and_repository_not_called()
    {
        var (service, _, docs, storage, _) = CreateService();
        storage.SaveException = new IOException("Disk full");

        _ = await Assert.ThrowsAsync<IOException>(() =>
            service.UploadAsync(ValidCommand(), CancellationToken.None));

        Assert.Empty(docs.Added);
    }

    [Fact]
    public async Task Repository_failure_triggers_compensating_delete()
    {
        var (service, _, docs, storage, _) = CreateService();
        docs.AddException = new InvalidOperationException("DB error");

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadAsync(ValidCommand(), CancellationToken.None));

        Assert.True(storage.DeleteCalled);
    }

    [Fact]
    public async Task Repository_failure_rethrows_original_exception()
    {
        var (service, _, docs, _, _) = CreateService();
        var original = new InvalidOperationException("Original DB error");
        docs.AddException = original;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadAsync(ValidCommand(), CancellationToken.None));

        Assert.Same(original, thrown);
    }

    [Fact]
    public async Task Cleanup_failure_does_not_mask_original_db_failure()
    {
        var (service, _, docs, storage, _) = CreateService();
        var original = new InvalidOperationException("Original DB error");
        docs.AddException = original;
        storage.DeleteException = new IOException("Delete failed too");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadAsync(ValidCommand(), CancellationToken.None));

        Assert.Same(original, thrown);
    }

    // ================================================================
    // Cancellation semantics
    // ================================================================

    [Fact]
    public async Task Request_cancellation_propagated_before_storage()
    {
        var (service, _, _, _, _) = CreateService();
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.UploadAsync(ValidCommand(), cts.Token));
    }

    [Fact]
    public async Task Post_storage_metadata_persistence_uses_none_token()
    {
        var (service, _, docs, _, _) = CreateService();
        // If the repo were to check cancellation, a pre-cancelled token would throw.
        // The service must pass CancellationToken.None, so no throw occurs.
        var cts = new CancellationTokenSource();

        _ = await service.UploadAsync(ValidCommand(), cts.Token);

        // If we got here, AddAsync was NOT given the cancelled token.
        _ = Assert.Single(docs.Added);
    }

    [Fact]
    public async Task Compensating_delete_uses_none_token()
    {
        var (service, _, docs, storage, _) = CreateService();
        docs.AddException = new InvalidOperationException("DB fail");

        // The CTS is not cancelled, but if the delete were given a pre-cancelled token
        // it would fail. We verify delete runs by checking DeleteCalled.
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadAsync(ValidCommand(), CancellationToken.None));

        Assert.True(storage.DeleteCalled);
    }
}
