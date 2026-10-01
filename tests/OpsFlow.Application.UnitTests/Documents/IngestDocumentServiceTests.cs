using OpsFlow.Application.Documents;
using OpsFlow.Application.UnitTests.TestSupport;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class IngestDocumentServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // ================================================================
    // Test wiring helpers
    // ================================================================

    private sealed class ServiceFixture
    {
        public FakeProjectRepository Projects { get; } = new() { ExistsResult = true };
        public FakeDocumentRepository Documents { get; } = new();
        public FakeDocumentStorage Storage { get; } = new();
        public FakeDocumentTextExtractor Extractor { get; } = new() { SupportedContentType = "text/plain" };
        public FakeDocumentExtractionRepository Extractions { get; } = new();
        public FakeDocumentChunkSetRepository ChunkSets { get; } = new();
        public FakeDocumentEmbeddingSetRepository EmbeddingSets { get; } = new();
        public FakeEmbeddingGenerator EmbeddingGenerator { get; } = new();
        public FixedClock Clock { get; } = new(Now);

        public IngestDocumentService BuildService(
            IDocumentChunkSnapshotReader? snapshotReaderOverride = null,
            IEmbeddingGenerator? generatorOverride = null)
        {
            var upload = new UploadDocumentService(Projects, Documents, Storage, Clock);

            var extract = new ExtractDocumentTextService(
                Documents, Extractions, Storage,
                [Extractor], Clock);

            var chunks = new EnsureDocumentChunksService(
                Documents, Extractions, ChunkSets,
                new FakeDocumentChunker(), Clock);

            var embeddings = new EnsureDocumentEmbeddingsService(
                Documents, snapshotReaderOverride ?? new FakeDocumentChunkSnapshotReader(),
                EmbeddingSets, generatorOverride ?? EmbeddingGenerator, Clock);

            return new IngestDocumentService(upload, extract, chunks, embeddings);
        }

        public void SetupSuccessfulPipeline()
        {
            var docId = Guid.NewGuid();
            var doc = new Document(docId, OrgId, ProjectId, "test.txt", "text/plain", 100, Now);
            Documents.GetByProjectResult = doc;

            Extractions.GetByDocumentResult = new DocumentExtraction(docId, "sample text", Now);
            ChunkSets.GetByDocumentResult = new DocumentChunkSet(docId, 1, 1, Now);
        }
    }

    /// <summary>
    /// Returns a snapshot whose DocumentId matches whatever the caller requests,
    /// so the embedding service's ValidateSnapshotCompleteness check passes
    /// regardless of the dynamic document ID created during upload.
    /// </summary>
    private sealed class MatchingSnapshotReader : IDocumentChunkSnapshotReader
    {
        public bool Enabled { get; set; } = true;

        public Task<DocumentChunkSnapshot?> GetByDocumentAsync(
            Guid documentId, Guid projectId, Guid organizationId,
            CancellationToken cancellationToken)
        {
            if (!Enabled)
            {
                return Task.FromResult<DocumentChunkSnapshot?>(null);
            }

            var snapshot = new DocumentChunkSnapshot(
                documentId, 1, 1,
                [new DocumentChunkSource(Guid.NewGuid(), 0, "sample text")]);
            return Task.FromResult<DocumentChunkSnapshot?>(snapshot);
        }
    }

    private sealed class MatchingEmbeddingSetRepository : IDocumentEmbeddingSetRepository
    {
        public bool ReturnExisting { get; set; } = true;

        public Task<DocumentEmbeddingSet?> GetByDocumentAndProfileAsync(
            Guid documentId, string profileId, Guid projectId, Guid organizationId,
            CancellationToken cancellationToken)
        {
            if (!ReturnExisting)
            {
                return Task.FromResult<DocumentEmbeddingSet?>(null);
            }

            var set = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1,
                EmbeddingProfiles.SemanticV1Id,
                EmbeddingProfiles.SemanticV1ModelId,
                EmbeddingProfiles.SemanticV1Dimensions,
                1, Now);
            return Task.FromResult<DocumentEmbeddingSet?>(set);
        }

        public Task<DocumentEmbeddingSetAddResult> AddIfAbsentAsync(
            DocumentEmbeddingSet embeddingSet,
            IReadOnlyList<ChunkEmbeddingInput> embeddings,
            Guid projectId,
            Guid organizationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(DocumentEmbeddingSetAddResult.Added(embeddingSet));
    }

    private static UploadDocumentCommand ValidCommand(
        Guid? orgId = null,
        Guid? projectId = null,
        string fileName = "notes.txt",
        long sizeBytes = 100,
        Stream? content = null)
    {
        return new(
            orgId ?? OrgId,
            projectId ?? ProjectId,
            fileName,
            null,
            sizeBytes,
            content ?? new MemoryStream([0x01]));
    }

    private static IngestDocumentService BuildFullPipelineService(ServiceFixture f)
    {
        f.SetupSuccessfulPipeline();
        var snapshotReader = new MatchingSnapshotReader();
        var embeddingSets = new MatchingEmbeddingSetRepository();

        var upload = new UploadDocumentService(f.Projects, f.Documents, f.Storage, f.Clock);
        var extract = new ExtractDocumentTextService(
            f.Documents, f.Extractions, f.Storage, [f.Extractor], f.Clock);
        var chunks = new EnsureDocumentChunksService(
            f.Documents, f.Extractions, f.ChunkSets, new FakeDocumentChunker(), f.Clock);
        var embeddings = new EnsureDocumentEmbeddingsService(
            f.Documents, snapshotReader, embeddingSets, f.EmbeddingGenerator, f.Clock);

        return new IngestDocumentService(upload, extract, chunks, embeddings);
    }

    // ================================================================
    // Constructor null guards
    // ================================================================

    [Fact]
    public void Constructor_rejects_null_upload_service()
    {
        var f = new ServiceFixture();
        var extract = new ExtractDocumentTextService(
            f.Documents, f.Extractions, f.Storage, [f.Extractor], f.Clock);
        var chunks = new EnsureDocumentChunksService(
            f.Documents, f.Extractions, f.ChunkSets, new FakeDocumentChunker(), f.Clock);
        var embeddings = new EnsureDocumentEmbeddingsService(
            f.Documents, new FakeDocumentChunkSnapshotReader(), f.EmbeddingSets,
            f.EmbeddingGenerator, f.Clock);

        Assert.Throws<ArgumentNullException>(() =>
            new IngestDocumentService(null!, extract, chunks, embeddings));
    }

    [Fact]
    public void Constructor_rejects_null_extract_service()
    {
        var f = new ServiceFixture();
        var upload = new UploadDocumentService(f.Projects, f.Documents, f.Storage, f.Clock);
        var chunks = new EnsureDocumentChunksService(
            f.Documents, f.Extractions, f.ChunkSets, new FakeDocumentChunker(), f.Clock);
        var embeddings = new EnsureDocumentEmbeddingsService(
            f.Documents, new FakeDocumentChunkSnapshotReader(), f.EmbeddingSets,
            f.EmbeddingGenerator, f.Clock);

        Assert.Throws<ArgumentNullException>(() =>
            new IngestDocumentService(upload, null!, chunks, embeddings));
    }

    [Fact]
    public void Constructor_rejects_null_chunks_service()
    {
        var f = new ServiceFixture();
        var upload = new UploadDocumentService(f.Projects, f.Documents, f.Storage, f.Clock);
        var extract = new ExtractDocumentTextService(
            f.Documents, f.Extractions, f.Storage, [f.Extractor], f.Clock);
        var embeddings = new EnsureDocumentEmbeddingsService(
            f.Documents, new FakeDocumentChunkSnapshotReader(), f.EmbeddingSets,
            f.EmbeddingGenerator, f.Clock);

        Assert.Throws<ArgumentNullException>(() =>
            new IngestDocumentService(upload, extract, null!, embeddings));
    }

    [Fact]
    public void Constructor_rejects_null_embeddings_service()
    {
        var f = new ServiceFixture();
        var upload = new UploadDocumentService(f.Projects, f.Documents, f.Storage, f.Clock);
        var extract = new ExtractDocumentTextService(
            f.Documents, f.Extractions, f.Storage, [f.Extractor], f.Clock);
        var chunks = new EnsureDocumentChunksService(
            f.Documents, f.Extractions, f.ChunkSets, new FakeDocumentChunker(), f.Clock);

        Assert.Throws<ArgumentNullException>(() =>
            new IngestDocumentService(upload, extract, chunks, null!));
    }

    // ================================================================
    // Null command guard
    // ================================================================

    [Fact]
    public async Task IngestAsync_rejects_null_command()
    {
        var f = new ServiceFixture();
        var sut = f.BuildService();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.IngestAsync(null!, CancellationToken.None));
    }

    // ================================================================
    // Successful full ingestion
    // ================================================================

    [Fact]
    public async Task IngestAsync_returns_FullyIngested_when_all_steps_succeed()
    {
        var f = new ServiceFixture();
        var sut = BuildFullPipelineService(f);

        var result = await sut.IngestAsync(ValidCommand(), CancellationToken.None);

        Assert.True(result.ProjectFound);
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Document);
        Assert.Equal(DocumentIngestionFailure.None, result.IngestionFailure);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task IngestAsync_persists_document_to_storage_and_repository()
    {
        var f = new ServiceFixture();
        var sut = BuildFullPipelineService(f);

        await sut.IngestAsync(ValidCommand(), CancellationToken.None);

        Assert.True(f.Storage.SaveCalled);
        Assert.Single(f.Documents.Added);
    }

    [Fact]
    public async Task IngestAsync_returns_document_with_correct_filename()
    {
        var f = new ServiceFixture();
        var sut = BuildFullPipelineService(f);

        var result = await sut.IngestAsync(
            ValidCommand(fileName: "notes.txt"), CancellationToken.None);

        Assert.Equal("notes.txt", result.Document!.OriginalFileName);
    }

    // ================================================================
    // Upload failures propagated
    // ================================================================

    [Fact]
    public async Task IngestAsync_returns_ProjectNotFound_when_project_does_not_exist()
    {
        var f = new ServiceFixture();
        f.Projects.ExistsResult = false;
        var sut = f.BuildService();

        var result = await sut.IngestAsync(ValidCommand(), CancellationToken.None);

        Assert.False(result.ProjectFound);
        Assert.False(result.Succeeded);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task IngestAsync_returns_upload_validation_error_for_unsupported_extension()
    {
        var f = new ServiceFixture();
        var sut = f.BuildService();

        var result = await sut.IngestAsync(
            ValidCommand(fileName: "evil.exe"), CancellationToken.None);

        Assert.True(result.ProjectFound);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Contains("Unsupported", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DocumentIngestionFailure.None, result.IngestionFailure);
    }

    [Fact]
    public async Task IngestAsync_returns_upload_validation_error_for_oversized_file()
    {
        var f = new ServiceFixture();
        var sut = f.BuildService();

        var result = await sut.IngestAsync(
            ValidCommand(sizeBytes: UploadDocumentService.MaxFileSizeBytes + 1),
            CancellationToken.None);

        Assert.True(result.ProjectFound);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Contains("MiB", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IngestAsync_does_not_call_extraction_when_upload_fails()
    {
        var f = new ServiceFixture();
        f.Projects.ExistsResult = false;
        var sut = f.BuildService();

        await sut.IngestAsync(ValidCommand(), CancellationToken.None);

        Assert.False(f.Extractions.GetByDocumentCalled);
        Assert.False(f.Extractor.ExtractCalled);
    }

    // ================================================================
    // Extraction content failures
    // ================================================================

    [Fact]
    public async Task IngestAsync_returns_ExtractionUnsupportedFormat_when_no_extractor_matches()
    {
        var f = new ServiceFixture();
        f.Extractor.SupportedContentType = "application/pdf";
        var doc = new Document(Guid.NewGuid(), OrgId, ProjectId, "test.txt", "text/plain", 100, Now);
        f.Documents.GetByProjectResult = doc;
        var sut = f.BuildService();

        var result = await sut.IngestAsync(ValidCommand(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Document);
        Assert.Equal(DocumentIngestionFailure.ExtractionUnsupportedFormat, result.IngestionFailure);
    }

    [Fact]
    public async Task IngestAsync_returns_ExtractionStorageMissing_when_storage_empty()
    {
        var f = new ServiceFixture();
        var doc = new Document(Guid.NewGuid(), OrgId, ProjectId, "test.txt", "text/plain", 100, Now);
        f.Documents.GetByProjectResult = doc;
        f.Storage.OpenReadResult = null;
        var sut = f.BuildService();

        var result = await sut.IngestAsync(ValidCommand(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Document);
        Assert.Equal(DocumentIngestionFailure.ExtractionStorageMissing, result.IngestionFailure);
    }

    [Fact]
    public async Task IngestAsync_returns_ExtractionMalformedDocument_when_content_unreadable()
    {
        var f = new ServiceFixture();
        var doc = new Document(Guid.NewGuid(), OrgId, ProjectId, "test.txt", "text/plain", 100, Now);
        f.Documents.GetByProjectResult = doc;
        f.Storage.OpenReadResult = new MemoryStream([0x01]);
        f.Extractor.ExtractResult = DocumentTextExtractionResult.MalformedDocument();
        var sut = f.BuildService();

        var result = await sut.IngestAsync(ValidCommand(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Document);
        Assert.Equal(DocumentIngestionFailure.ExtractionMalformedDocument, result.IngestionFailure);
    }

    [Fact]
    public async Task IngestAsync_returns_ExtractionLimitExceeded_when_text_too_large()
    {
        var f = new ServiceFixture();
        var doc = new Document(Guid.NewGuid(), OrgId, ProjectId, "test.txt", "text/plain", 100, Now);
        f.Documents.GetByProjectResult = doc;
        f.Storage.OpenReadResult = new MemoryStream([0x01]);
        f.Extractor.ExtractResult = DocumentTextExtractionResult.LimitExceeded();
        var sut = f.BuildService();

        var result = await sut.IngestAsync(ValidCommand(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Document);
        Assert.Equal(DocumentIngestionFailure.ExtractionLimitExceeded, result.IngestionFailure);
    }

    [Fact]
    public async Task IngestAsync_does_not_call_chunking_when_extraction_fails()
    {
        var f = new ServiceFixture();
        f.Extractor.SupportedContentType = "application/pdf";
        var doc = new Document(Guid.NewGuid(), OrgId, ProjectId, "test.txt", "text/plain", 100, Now);
        f.Documents.GetByProjectResult = doc;
        var sut = f.BuildService();

        await sut.IngestAsync(ValidCommand(), CancellationToken.None);

        Assert.False(f.ChunkSets.GetByDocumentCalled);
    }

    // ================================================================
    // Provider exception propagation
    // ================================================================

    [Fact]
    public async Task IngestAsync_propagates_EmbeddingGenerationException()
    {
        var f = new ServiceFixture();
        f.SetupSuccessfulPipeline();

        var snapshotReader = new MatchingSnapshotReader();
        var embeddingSets = new MatchingEmbeddingSetRepository { ReturnExisting = false };

        var throwing = new ThrowingEmbeddingGenerator(
            new EmbeddingGenerationException("provider failure"));

        var upload = new UploadDocumentService(f.Projects, f.Documents, f.Storage, f.Clock);
        var extract = new ExtractDocumentTextService(
            f.Documents, f.Extractions, f.Storage, [f.Extractor], f.Clock);
        var chunks = new EnsureDocumentChunksService(
            f.Documents, f.Extractions, f.ChunkSets, new FakeDocumentChunker(), f.Clock);
        var embeddings = new EnsureDocumentEmbeddingsService(
            f.Documents, snapshotReader, embeddingSets, throwing, f.Clock);

        var sut = new IngestDocumentService(upload, extract, chunks, embeddings);

        var ex = await Assert.ThrowsAsync<EmbeddingGenerationException>(() =>
            sut.IngestAsync(ValidCommand(), CancellationToken.None));

        Assert.Equal("provider failure", ex.Message);
    }

    [Fact]
    public async Task IngestAsync_document_persists_even_when_embedding_fails()
    {
        var f = new ServiceFixture();
        f.SetupSuccessfulPipeline();

        var snapshotReader = new MatchingSnapshotReader();
        var embeddingSets = new MatchingEmbeddingSetRepository { ReturnExisting = false };

        var throwing = new ThrowingEmbeddingGenerator(
            new EmbeddingGenerationException("provider failure"));

        var upload = new UploadDocumentService(f.Projects, f.Documents, f.Storage, f.Clock);
        var extract = new ExtractDocumentTextService(
            f.Documents, f.Extractions, f.Storage, [f.Extractor], f.Clock);
        var chunks = new EnsureDocumentChunksService(
            f.Documents, f.Extractions, f.ChunkSets, new FakeDocumentChunker(), f.Clock);
        var embeddings = new EnsureDocumentEmbeddingsService(
            f.Documents, snapshotReader, embeddingSets, throwing, f.Clock);

        var sut = new IngestDocumentService(upload, extract, chunks, embeddings);

        await Record.ExceptionAsync(() =>
            sut.IngestAsync(ValidCommand(), CancellationToken.None));

        Assert.Single(f.Documents.Added);
        Assert.True(f.Storage.SaveCalled);
    }

    // ================================================================
    // Cancellation propagation
    // ================================================================

    [Fact]
    public async Task IngestAsync_propagates_cancellation()
    {
        var f = new ServiceFixture();
        var sut = f.BuildService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.IngestAsync(ValidCommand(), cts.Token));
    }

    // ================================================================
    // Idempotency: SuccessExisting statuses proceed normally
    // ================================================================

    [Fact]
    public async Task IngestAsync_succeeds_when_all_downstream_services_return_SuccessExisting()
    {
        var f = new ServiceFixture();
        var sut = BuildFullPipelineService(f);

        var result = await sut.IngestAsync(ValidCommand(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(DocumentIngestionFailure.None, result.IngestionFailure);
    }

    // ================================================================
    // Internal helpers for test isolation
    // ================================================================

    private sealed class ThrowingEmbeddingGenerator(Exception exception) : IEmbeddingGenerator
    {
        public EmbeddingGeneratorIdentity Identity { get; } = new(
            EmbeddingProfiles.SemanticV1Id,
            EmbeddingProfiles.SemanticV1ModelId,
            EmbeddingProfiles.SemanticV1Dimensions);

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken) =>
            throw exception;
    }
}
