using OpsFlow.Application.Documents;
using OpsFlow.Application.UnitTests.TestSupport;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class EnsureDocumentChunksServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid DocumentId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

    private static Document MakeDocument() =>
        new(DocumentId, OrgId, ProjectId, "report.pdf", "application/pdf", 4096, Now);

    private static DocumentExtraction MakeExtraction(string text = "Hello world") =>
        new(DocumentId, text, Now);

    private static (
        EnsureDocumentChunksService Service,
        FakeDocumentRepository Documents,
        FakeDocumentExtractionRepository Extractions,
        FakeDocumentChunkSetRepository ChunkSets,
        FakeDocumentChunker Chunker,
        FixedClock Clock)
        CreateService(Document? documentResult = null)
    {
        var documents = new FakeDocumentRepository { GetByProjectResult = documentResult };
        var extractions = new FakeDocumentExtractionRepository();
        var chunkSets = new FakeDocumentChunkSetRepository();
        var chunker = new FakeDocumentChunker();
        var clock = new FixedClock(Now);
        var service = new EnsureDocumentChunksService(
            documents, extractions, chunkSets, chunker, clock);
        return (service, documents, extractions, chunkSets, chunker, clock);
    }

    private static EnsureDocumentChunksCommand MakeCommand() =>
        new(OrgId, ProjectId, DocumentId);

    // ================================================================
    // Constructor guards
    // ================================================================

    [Fact]
    public void Constructor_rejects_null_document_repository()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EnsureDocumentChunksService(
                null!,
                new FakeDocumentExtractionRepository(),
                new FakeDocumentChunkSetRepository(),
                new FakeDocumentChunker(),
                new FixedClock(Now)));
    }

    [Fact]
    public void Constructor_rejects_null_extraction_repository()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EnsureDocumentChunksService(
                new FakeDocumentRepository(),
                null!,
                new FakeDocumentChunkSetRepository(),
                new FakeDocumentChunker(),
                new FixedClock(Now)));
    }

    [Fact]
    public void Constructor_rejects_null_chunk_set_repository()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EnsureDocumentChunksService(
                new FakeDocumentRepository(),
                new FakeDocumentExtractionRepository(),
                null!,
                new FakeDocumentChunker(),
                new FixedClock(Now)));
    }

    [Fact]
    public void Constructor_rejects_null_chunker()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EnsureDocumentChunksService(
                new FakeDocumentRepository(),
                new FakeDocumentExtractionRepository(),
                new FakeDocumentChunkSetRepository(),
                null!,
                new FixedClock(Now)));
    }

    [Fact]
    public void Constructor_rejects_null_clock()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EnsureDocumentChunksService(
                new FakeDocumentRepository(),
                new FakeDocumentExtractionRepository(),
                new FakeDocumentChunkSetRepository(),
                new FakeDocumentChunker(),
                null!));
    }

    // ================================================================
    // Input validation
    // ================================================================

    [Fact]
    public async Task Throws_for_empty_organization_id()
    {
        var (service, _, _, _, _, _) = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.EnsureAsync(
                new EnsureDocumentChunksCommand(Guid.Empty, ProjectId, DocumentId),
                CancellationToken.None));
    }

    [Fact]
    public async Task Returns_not_found_for_empty_project_id()
    {
        var (service, _, _, _, _, _) = CreateService();

        var result = await service.EnsureAsync(
            new EnsureDocumentChunksCommand(OrgId, Guid.Empty, DocumentId),
            CancellationToken.None);

        Assert.Equal(EnsureDocumentChunksStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Returns_not_found_for_empty_document_id()
    {
        var (service, _, _, _, _, _) = CreateService();

        var result = await service.EnsureAsync(
            new EnsureDocumentChunksCommand(OrgId, ProjectId, Guid.Empty),
            CancellationToken.None);

        Assert.Equal(EnsureDocumentChunksStatus.NotFound, result.Status);
    }

    // ================================================================
    // Document not found
    // ================================================================

    [Fact]
    public async Task Returns_not_found_when_document_does_not_exist()
    {
        var (service, _, _, _, _, _) = CreateService(documentResult: null);

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentChunksStatus.NotFound, result.Status);
    }

    // ================================================================
    // Cached chunk set
    // ================================================================

    [Fact]
    public async Task Returns_success_existing_when_chunk_set_cached()
    {
        var doc = MakeDocument();
        var (service, _, _, chunkSets, chunker, _) = CreateService(doc);
        var cached = new DocumentChunkSet(DocumentId, 1, 3, Now);
        chunkSets.GetByDocumentResult = cached;

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentChunksStatus.SuccessExisting, result.Status);
        Assert.Same(cached, result.ChunkSet);
    }

    [Fact]
    public async Task Cached_chunk_set_does_not_call_chunker()
    {
        var doc = MakeDocument();
        var (service, _, _, chunkSets, chunker, _) = CreateService(doc);
        chunkSets.GetByDocumentResult = new DocumentChunkSet(DocumentId, 1, 3, Now);

        _ = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.False(chunker.ChunkCalled);
    }

    [Fact]
    public async Task Cached_chunk_set_does_not_read_extraction()
    {
        var doc = MakeDocument();
        var (service, _, extractions, chunkSets, _, _) = CreateService(doc);
        chunkSets.GetByDocumentResult = new DocumentChunkSet(DocumentId, 1, 3, Now);

        _ = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.False(extractions.GetByDocumentCalled);
    }

    // ================================================================
    // Extraction not found
    // ================================================================

    [Fact]
    public async Task Returns_extraction_not_found_when_no_extraction_exists()
    {
        var doc = MakeDocument();
        var (service, _, extractions, _, _, _) = CreateService(doc);
        extractions.GetByDocumentResult = null;

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentChunksStatus.ExtractionNotFound, result.Status);
    }

    // ================================================================
    // Successful first chunking
    // ================================================================

    [Fact]
    public async Task Returns_success_created_on_first_chunking()
    {
        var doc = MakeDocument();
        var (service, _, extractions, chunkSets, chunker, _) = CreateService(doc);
        extractions.GetByDocumentResult = MakeExtraction("Hello world");
        chunker.ChunkResult = [new DocumentChunkSlice(0, 11)];

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentChunksStatus.SuccessCreated, result.Status);
        Assert.NotNull(result.ChunkSet);
    }

    [Fact]
    public async Task First_chunking_uses_clock_for_created_at()
    {
        var doc = MakeDocument();
        var (service, _, extractions, chunkSets, chunker, clock) = CreateService(doc);
        extractions.GetByDocumentResult = MakeExtraction("Hello world");
        chunker.ChunkResult = [new DocumentChunkSlice(0, 11)];

        _ = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(clock.UtcNow, chunkSets.LastAddedChunkSet!.CreatedAt);
    }

    [Fact]
    public async Task First_chunking_sets_chunking_version()
    {
        var doc = MakeDocument();
        var (service, _, extractions, chunkSets, chunker, _) = CreateService(doc);
        extractions.GetByDocumentResult = MakeExtraction("Hello world");
        chunker.ChunkResult = [new DocumentChunkSlice(0, 11)];

        _ = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentChunksService.ChunkingVersion, chunkSets.LastAddedChunkSet!.ChunkingVersion);
    }

    [Fact]
    public async Task First_chunking_sets_correct_chunk_count()
    {
        var doc = MakeDocument();
        var (service, _, extractions, chunkSets, chunker, _) = CreateService(doc);
        extractions.GetByDocumentResult = MakeExtraction("Hello world");
        chunker.ChunkResult = [new DocumentChunkSlice(0, 5), new DocumentChunkSlice(3, 11)];

        _ = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(2, chunkSets.LastAddedChunkSet!.ChunkCount);
    }

    [Fact]
    public async Task First_chunking_produces_correct_chunk_entities()
    {
        var doc = MakeDocument();
        var (service, _, extractions, chunkSets, chunker, _) = CreateService(doc);
        var extractionText = "Hello world";
        extractions.GetByDocumentResult = MakeExtraction(extractionText);
        chunker.ChunkResult = [new DocumentChunkSlice(0, 5), new DocumentChunkSlice(3, 11)];

        _ = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        var chunks = chunkSets.LastAddedChunks!;
        Assert.Equal(2, chunks.Count);

        Assert.Equal(0, chunks[0].ChunkIndex);
        Assert.Equal(0, chunks[0].StartOffset);
        Assert.Equal(5, chunks[0].EndOffset);
        Assert.Equal("Hello", chunks[0].Text);

        Assert.Equal(1, chunks[1].ChunkIndex);
        Assert.Equal(3, chunks[1].StartOffset);
        Assert.Equal(11, chunks[1].EndOffset);
        Assert.Equal("lo world", chunks[1].Text);
    }

    [Fact]
    public async Task First_chunking_passes_extraction_text_to_chunker()
    {
        var doc = MakeDocument();
        var (service, _, extractions, _, chunker, _) = CreateService(doc);
        extractions.GetByDocumentResult = MakeExtraction("Hello world");
        chunker.ChunkResult = [new DocumentChunkSlice(0, 11)];

        _ = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal("Hello world", chunker.ReceivedText);
    }

    // ================================================================
    // Empty extraction
    // ================================================================

    [Fact]
    public async Task Empty_extraction_creates_chunk_set_with_zero_count()
    {
        var doc = MakeDocument();
        var (service, _, extractions, chunkSets, chunker, _) = CreateService(doc);
        extractions.GetByDocumentResult = MakeExtraction(string.Empty);
        chunker.ChunkResult = [];

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentChunksStatus.SuccessCreated, result.Status);
        Assert.Equal(0, chunkSets.LastAddedChunkSet!.ChunkCount);
        Assert.Empty(chunkSets.LastAddedChunks!);
    }

    // ================================================================
    // Concurrent insert
    // ================================================================

    [Fact]
    public async Task Concurrent_insert_returns_success_existing()
    {
        var doc = MakeDocument();
        var (service, _, extractions, chunkSets, chunker, _) = CreateService(doc);
        extractions.GetByDocumentResult = MakeExtraction("Hello world");
        chunker.ChunkResult = [new DocumentChunkSlice(0, 11)];
        var existing = new DocumentChunkSet(DocumentId, 1, 1, Now);
        chunkSets.AddIfAbsentResult = DocumentChunkSetAddResult.AlreadyExists(existing);

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentChunksStatus.SuccessExisting, result.Status);
        Assert.Same(existing, result.ChunkSet);
    }

    // ================================================================
    // Tenant isolation
    // ================================================================

    [Fact]
    public async Task AddIfAbsent_receives_project_and_organization_ids()
    {
        var doc = MakeDocument();
        var (service, _, extractions, chunkSets, chunker, _) = CreateService(doc);
        extractions.GetByDocumentResult = MakeExtraction("Hello world");
        chunker.ChunkResult = [new DocumentChunkSlice(0, 11)];

        _ = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(ProjectId, chunkSets.ReceivedAddProjectId);
        Assert.Equal(OrgId, chunkSets.ReceivedAddOrganizationId);
    }
}
