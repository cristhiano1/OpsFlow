using OpsFlow.Application.Documents;
using OpsFlow.Application.UnitTests.TestSupport;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class ExtractDocumentTextServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid DocumentId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

    private static Document MakeDocument(string contentType = "application/pdf") =>
        new(DocumentId, OrgId, ProjectId, "report.pdf", contentType, 4096, Now);

    private static (
        ExtractDocumentTextService Service,
        FakeDocumentRepository Documents,
        FakeDocumentExtractionRepository Extractions,
        FakeDocumentStorage Storage,
        FakeDocumentTextExtractor Extractor,
        FixedClock Clock)
        CreateService(Document? documentResult = null, string extractorContentType = "application/pdf")
    {
        var documents = new FakeDocumentRepository { GetByProjectResult = documentResult };
        var extractions = new FakeDocumentExtractionRepository();
        var storage = new FakeDocumentStorage();
        var extractor = new FakeDocumentTextExtractor { SupportedContentType = extractorContentType };
        var clock = new FixedClock(Now);
        var service = new ExtractDocumentTextService(
            documents, extractions, storage, [extractor], clock);
        return (service, documents, extractions, storage, extractor, clock);
    }

    private static ExtractDocumentTextCommand MakeCommand() =>
        new(OrgId, ProjectId, DocumentId);

    // ================================================================
    // Constructor guards
    // ================================================================

    [Fact]
    public void Constructor_rejects_null_document_repository()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ExtractDocumentTextService(
                null!, new FakeDocumentExtractionRepository(),
                new FakeDocumentStorage(), [], new FixedClock(Now)));
    }

    [Fact]
    public void Constructor_rejects_null_extraction_repository()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ExtractDocumentTextService(
                new FakeDocumentRepository(), null!,
                new FakeDocumentStorage(), [], new FixedClock(Now)));
    }

    [Fact]
    public void Constructor_rejects_null_storage()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ExtractDocumentTextService(
                new FakeDocumentRepository(), new FakeDocumentExtractionRepository(),
                null!, [], new FixedClock(Now)));
    }

    [Fact]
    public void Constructor_rejects_null_extractors()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ExtractDocumentTextService(
                new FakeDocumentRepository(), new FakeDocumentExtractionRepository(),
                new FakeDocumentStorage(), null!, new FixedClock(Now)));
    }

    [Fact]
    public void Constructor_rejects_null_clock()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ExtractDocumentTextService(
                new FakeDocumentRepository(), new FakeDocumentExtractionRepository(),
                new FakeDocumentStorage(), [], null!));
    }

    // ================================================================
    // Input validation
    // ================================================================

    [Fact]
    public async Task Throws_for_empty_organization_id()
    {
        var (service, _, _, _, _, _) = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExtractAsync(
                new ExtractDocumentTextCommand(Guid.Empty, ProjectId, DocumentId),
                CancellationToken.None));
    }

    [Fact]
    public async Task Returns_not_found_for_empty_project_id()
    {
        var (service, _, _, _, _, _) = CreateService();

        var result = await service.ExtractAsync(
            new ExtractDocumentTextCommand(OrgId, Guid.Empty, DocumentId),
            CancellationToken.None);

        Assert.Equal(ExtractDocumentTextStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Returns_not_found_for_empty_document_id()
    {
        var (service, _, _, _, _, _) = CreateService();

        var result = await service.ExtractAsync(
            new ExtractDocumentTextCommand(OrgId, ProjectId, Guid.Empty),
            CancellationToken.None);

        Assert.Equal(ExtractDocumentTextStatus.NotFound, result.Status);
    }

    // ================================================================
    // Document not found
    // ================================================================

    [Fact]
    public async Task Returns_not_found_when_document_does_not_exist()
    {
        var (service, _, _, _, _, _) = CreateService(documentResult: null);

        var result = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(ExtractDocumentTextStatus.NotFound, result.Status);
    }

    // ================================================================
    // Cached extraction
    // ================================================================

    [Fact]
    public async Task Returns_success_existing_when_extraction_cached()
    {
        var doc = MakeDocument();
        var (service, _, extractions, storage, extractor, _) = CreateService(doc);
        var cached = new DocumentExtraction(DocumentId, "cached text", Now);
        extractions.GetByDocumentResult = cached;

        var result = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(ExtractDocumentTextStatus.SuccessExisting, result.Status);
        Assert.Same(cached, result.Extraction);
    }

    [Fact]
    public async Task Existing_extraction_does_not_open_storage()
    {
        var doc = MakeDocument();
        var (service, _, extractions, storage, _, _) = CreateService(doc);
        extractions.GetByDocumentResult = new DocumentExtraction(DocumentId, "cached", Now);

        _ = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.False(storage.OpenReadCalled);
    }

    [Fact]
    public async Task Existing_extraction_does_not_call_add()
    {
        var doc = MakeDocument();
        var (service, _, extractions, _, _, _) = CreateService(doc);
        extractions.GetByDocumentResult = new DocumentExtraction(DocumentId, "cached", Now);

        _ = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.False(extractions.AddIfAbsentCalled);
    }

    [Fact]
    public async Task Existing_extraction_does_not_call_extractor()
    {
        var doc = MakeDocument();
        var (service, _, extractions, _, extractor, _) = CreateService(doc);
        extractions.GetByDocumentResult = new DocumentExtraction(DocumentId, "cached", Now);

        _ = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.False(extractor.ExtractCalled);
    }

    // ================================================================
    // Unsupported format
    // ================================================================

    [Fact]
    public async Task Returns_unsupported_format_when_no_extractor_matches()
    {
        var doc = MakeDocument(contentType: "application/octet-stream");
        var (service, _, _, _, _, _) = CreateService(doc, extractorContentType: "application/pdf");

        var result = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(ExtractDocumentTextStatus.UnsupportedFormat, result.Status);
    }

    // ================================================================
    // Storage missing
    // ================================================================

    [Fact]
    public async Task Returns_storage_missing_when_storage_returns_null()
    {
        var doc = MakeDocument();
        var (service, _, _, storage, _, _) = CreateService(doc);
        storage.OpenReadResult = null;

        var result = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(ExtractDocumentTextStatus.StorageMissing, result.Status);
    }

    [Fact]
    public async Task Storage_not_opened_before_document_metadata_lookup()
    {
        var (service, documents, _, storage, _, _) = CreateService(documentResult: null);

        _ = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.False(storage.OpenReadCalled);
        Assert.True(documents.ReceivedGetDocumentId.HasValue);
    }

    // ================================================================
    // Malformed / Limit
    // ================================================================

    [Fact]
    public async Task Returns_malformed_when_extractor_reports_malformed()
    {
        var doc = MakeDocument();
        var (service, _, _, storage, extractor, _) = CreateService(doc);
        storage.OpenReadResult = new MemoryStream([0x01]);
        extractor.ExtractResult = DocumentTextExtractionResult.MalformedDocument();

        var result = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(ExtractDocumentTextStatus.MalformedDocument, result.Status);
    }

    [Fact]
    public async Task Returns_limit_exceeded_when_extractor_reports_limit()
    {
        var doc = MakeDocument();
        var (service, _, _, storage, extractor, _) = CreateService(doc);
        storage.OpenReadResult = new MemoryStream([0x01]);
        extractor.ExtractResult = DocumentTextExtractionResult.LimitExceeded();

        var result = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(ExtractDocumentTextStatus.ExtractionLimitExceeded, result.Status);
    }

    // ================================================================
    // Successful first extraction
    // ================================================================

    [Fact]
    public async Task Returns_success_created_on_first_extraction()
    {
        var doc = MakeDocument();
        var (service, _, _, storage, extractor, _) = CreateService(doc);
        storage.OpenReadResult = new MemoryStream([0x01]);
        extractor.ExtractResult = DocumentTextExtractionResult.Success("extracted text");

        var result = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(ExtractDocumentTextStatus.SuccessCreated, result.Status);
        Assert.NotNull(result.Extraction);
        Assert.Equal("extracted text", result.Extraction.ExtractedText);
    }

    [Fact]
    public async Task First_extraction_uses_clock_for_extracted_at()
    {
        var doc = MakeDocument();
        var (service, _, _, storage, extractor, clock) = CreateService(doc);
        storage.OpenReadResult = new MemoryStream([0x01]);
        extractor.ExtractResult = DocumentTextExtractionResult.Success("text");

        var result = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(clock.UtcNow, result.Extraction!.ExtractedAt);
    }

    [Fact]
    public async Task First_extraction_passes_correct_document_storage_address()
    {
        var doc = MakeDocument();
        var (service, _, _, storage, extractor, _) = CreateService(doc);
        storage.OpenReadResult = new MemoryStream([0x01]);
        extractor.ExtractResult = DocumentTextExtractionResult.Success("text");

        _ = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.NotNull(storage.LastOpenReadAddress);
        Assert.Equal(OrgId, storage.LastOpenReadAddress.OrganizationId);
        Assert.Equal(ProjectId, storage.LastOpenReadAddress.ProjectId);
        Assert.Equal(DocumentId, storage.LastOpenReadAddress.DocumentId);
    }

    [Fact]
    public async Task Normalization_applied_to_extracted_text()
    {
        var doc = MakeDocument();
        var (service, _, _, storage, extractor, _) = CreateService(doc);
        storage.OpenReadResult = new MemoryStream([0x01]);
        extractor.ExtractResult = DocumentTextExtractionResult.Success("  hello\r\nworld\r  ");

        var result = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal("hello\nworld", result.Extraction!.ExtractedText);
    }

    [Fact]
    public async Task Empty_extraction_text_is_valid_success()
    {
        var doc = MakeDocument();
        var (service, _, _, storage, extractor, _) = CreateService(doc);
        storage.OpenReadResult = new MemoryStream([0x01]);
        extractor.ExtractResult = DocumentTextExtractionResult.Success(string.Empty);

        var result = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(ExtractDocumentTextStatus.SuccessCreated, result.Status);
        Assert.Equal(string.Empty, result.Extraction!.ExtractedText);
    }

    // ================================================================
    // Concurrent insert
    // ================================================================

    [Fact]
    public async Task Concurrent_insert_returns_success_existing()
    {
        var doc = MakeDocument();
        var (service, _, extractions, storage, extractor, _) = CreateService(doc);
        storage.OpenReadResult = new MemoryStream([0x01]);
        extractor.ExtractResult = DocumentTextExtractionResult.Success("text");
        var existing = new DocumentExtraction(DocumentId, "existing text", Now);
        extractions.AddIfAbsentResult = DocumentExtractionAddResult.AlreadyExists(existing);

        var result = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(ExtractDocumentTextStatus.SuccessExisting, result.Status);
        Assert.Same(existing, result.Extraction);
    }

    // ================================================================
    // Stream disposal
    // ================================================================

    [Fact]
    public async Task Stream_disposed_after_success()
    {
        var doc = MakeDocument();
        var (service, _, _, storage, extractor, _) = CreateService(doc);
        var stream = new TrackingStream([0x01]);
        storage.OpenReadResult = stream;
        extractor.ExtractResult = DocumentTextExtractionResult.Success("text");

        _ = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task Stream_disposed_after_malformed()
    {
        var doc = MakeDocument();
        var (service, _, _, storage, extractor, _) = CreateService(doc);
        var stream = new TrackingStream([0x01]);
        storage.OpenReadResult = stream;
        extractor.ExtractResult = DocumentTextExtractionResult.MalformedDocument();

        _ = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task Stream_disposed_after_limit_exceeded()
    {
        var doc = MakeDocument();
        var (service, _, _, storage, extractor, _) = CreateService(doc);
        var stream = new TrackingStream([0x01]);
        storage.OpenReadResult = stream;
        extractor.ExtractResult = DocumentTextExtractionResult.LimitExceeded();

        _ = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.True(stream.WasDisposed);
    }

    // ================================================================
    // No EF/SQL concepts
    // ================================================================

    [Fact]
    public async Task AddIfAbsent_receives_project_and_organization_ids()
    {
        var doc = MakeDocument();
        var (service, _, extractions, storage, extractor, _) = CreateService(doc);
        storage.OpenReadResult = new MemoryStream([0x01]);
        extractor.ExtractResult = DocumentTextExtractionResult.Success("text");

        _ = await service.ExtractAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(ProjectId, extractions.ReceivedAddProjectId);
        Assert.Equal(OrgId, extractions.ReceivedAddOrganizationId);
    }

    // ================================================================
    // Helper
    // ================================================================

    private sealed class TrackingStream : MemoryStream
    {
        public bool WasDisposed { get; private set; }

        public TrackingStream(byte[] data) : base(data) { }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
