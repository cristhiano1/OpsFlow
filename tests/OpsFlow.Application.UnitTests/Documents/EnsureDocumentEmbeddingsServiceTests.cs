using OpsFlow.Application.Documents;
using OpsFlow.Application.UnitTests.TestSupport;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class EnsureDocumentEmbeddingsServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid DocumentId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);

    private static Document MakeDocument() =>
        new(DocumentId, OrgId, ProjectId, "report.pdf", "application/pdf", 4096, Now);

    private static DocumentChunkSnapshot MakeSnapshot(int chunkCount = 3) =>
        new(DocumentId, 1, chunkCount,
            [.. Enumerable.Range(0, chunkCount)
                .Select(i => new DocumentChunkSource(Guid.NewGuid(), i, $"Chunk text {i}"))]);

    private static DocumentEmbeddingSet MakeExistingEmbeddingSet(
        string profileId = "opsflow-semantic-v1",
        string modelId = "text-embedding-3-small",
        int dimensions = 1536,
        int chunkingVersion = 1,
        int embeddingCount = 3) =>
        new(Guid.NewGuid(), DocumentId, chunkingVersion, profileId, modelId, dimensions, embeddingCount, Now);

    private static (
        EnsureDocumentEmbeddingsService Service,
        FakeDocumentRepository Documents,
        FakeDocumentChunkSnapshotReader SnapshotReader,
        FakeDocumentEmbeddingSetRepository EmbeddingSets,
        FakeEmbeddingGenerator Generator,
        FixedClock Clock)
        CreateService(Document? documentResult = null)
    {
        var documents = new FakeDocumentRepository { GetByProjectResult = documentResult };
        var snapshotReader = new FakeDocumentChunkSnapshotReader();
        var embeddingSets = new FakeDocumentEmbeddingSetRepository();
        var generator = new FakeEmbeddingGenerator();
        var clock = new FixedClock(Now);
        var service = new EnsureDocumentEmbeddingsService(
            documents, snapshotReader, embeddingSets, generator, clock);
        return (service, documents, snapshotReader, embeddingSets, generator, clock);
    }

    private static EnsureDocumentEmbeddingsCommand MakeCommand() =>
        new(OrgId, ProjectId, DocumentId);

    // ================================================================
    // Constructor guards
    // ================================================================

    [Fact]
    public void Constructor_rejects_null_document_repository()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EnsureDocumentEmbeddingsService(
                null!,
                new FakeDocumentChunkSnapshotReader(),
                new FakeDocumentEmbeddingSetRepository(),
                new FakeEmbeddingGenerator(),
                new FixedClock(Now)));
    }

    [Fact]
    public void Constructor_rejects_null_snapshot_reader()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EnsureDocumentEmbeddingsService(
                new FakeDocumentRepository(),
                null!,
                new FakeDocumentEmbeddingSetRepository(),
                new FakeEmbeddingGenerator(),
                new FixedClock(Now)));
    }

    [Fact]
    public void Constructor_rejects_null_embedding_set_repository()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EnsureDocumentEmbeddingsService(
                new FakeDocumentRepository(),
                new FakeDocumentChunkSnapshotReader(),
                null!,
                new FakeEmbeddingGenerator(),
                new FixedClock(Now)));
    }

    [Fact]
    public void Constructor_rejects_null_generator()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EnsureDocumentEmbeddingsService(
                new FakeDocumentRepository(),
                new FakeDocumentChunkSnapshotReader(),
                new FakeDocumentEmbeddingSetRepository(),
                null!,
                new FixedClock(Now)));
    }

    [Fact]
    public void Constructor_rejects_null_clock()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EnsureDocumentEmbeddingsService(
                new FakeDocumentRepository(),
                new FakeDocumentChunkSnapshotReader(),
                new FakeDocumentEmbeddingSetRepository(),
                new FakeEmbeddingGenerator(),
                null!));
    }

    // ================================================================
    // Command validation
    // ================================================================

    [Fact]
    public async Task EnsureAsync_rejects_null_command()
    {
        var (service, _, _, _, _, _) = CreateService();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.EnsureAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAsync_throws_for_empty_organization_id()
    {
        var (service, _, _, _, _, _) = CreateService();
        var command = new EnsureDocumentEmbeddingsCommand(Guid.Empty, ProjectId, DocumentId);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.EnsureAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAsync_returns_NotFound_for_empty_project_id()
    {
        var (service, _, _, _, _, _) = CreateService();
        var command = new EnsureDocumentEmbeddingsCommand(OrgId, Guid.Empty, DocumentId);

        var result = await service.EnsureAsync(command, CancellationToken.None);

        Assert.Equal(EnsureDocumentEmbeddingsStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task EnsureAsync_returns_NotFound_for_empty_document_id()
    {
        var (service, _, _, _, _, _) = CreateService();
        var command = new EnsureDocumentEmbeddingsCommand(OrgId, ProjectId, Guid.Empty);

        var result = await service.EnsureAsync(command, CancellationToken.None);

        Assert.Equal(EnsureDocumentEmbeddingsStatus.NotFound, result.Status);
    }

    // ================================================================
    // Generator profile validation
    // ================================================================

    [Fact]
    public async Task EnsureAsync_throws_for_wrong_profile_id()
    {
        var (service, _, _, _, generator, _) = CreateService(MakeDocument());
        generator.Identity = new EmbeddingGeneratorIdentity(
            "wrong-profile", "model", EmbeddingProfiles.SemanticV1Dimensions);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureAsync(MakeCommand(), CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAsync_throws_for_wrong_dimensions()
    {
        var (service, _, _, _, generator, _) = CreateService(MakeDocument());
        generator.Identity = new EmbeddingGeneratorIdentity(
            EmbeddingProfiles.SemanticV1Id, "model", 768);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureAsync(MakeCommand(), CancellationToken.None));
    }

    // ================================================================
    // Document not found
    // ================================================================

    [Fact]
    public async Task EnsureAsync_returns_NotFound_when_document_missing()
    {
        var (service, _, _, _, _, _) = CreateService(documentResult: null);

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentEmbeddingsStatus.NotFound, result.Status);
    }

    // ================================================================
    // Chunks not found
    // ================================================================

    [Fact]
    public async Task EnsureAsync_returns_ChunksNotFound_when_snapshot_null()
    {
        var (service, _, snapshotReader, _, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = null;

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentEmbeddingsStatus.ChunksNotFound, result.Status);
    }

    // ================================================================
    // Snapshot completeness validation
    // ================================================================

    [Fact]
    public async Task EnsureAsync_throws_when_snapshot_document_id_mismatch()
    {
        var (service, _, snapshotReader, _, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = new DocumentChunkSnapshot(
            Guid.NewGuid(), 1, 1,
            [new DocumentChunkSource(Guid.NewGuid(), 0, "text")]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureAsync(MakeCommand(), CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAsync_throws_when_chunk_count_mismatch()
    {
        var (service, _, snapshotReader, _, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = new DocumentChunkSnapshot(
            DocumentId, 1, 5,
            [new DocumentChunkSource(Guid.NewGuid(), 0, "text")]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureAsync(MakeCommand(), CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAsync_throws_when_chunk_index_noncontiguous()
    {
        var (service, _, snapshotReader, _, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = new DocumentChunkSnapshot(
            DocumentId, 1, 2,
            [
                new DocumentChunkSource(Guid.NewGuid(), 0, "a"),
                new DocumentChunkSource(Guid.NewGuid(), 5, "b"),
            ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureAsync(MakeCommand(), CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAsync_throws_when_chunk_id_empty()
    {
        var (service, _, snapshotReader, _, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = new DocumentChunkSnapshot(
            DocumentId, 1, 1,
            [new DocumentChunkSource(Guid.Empty, 0, "text")]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureAsync(MakeCommand(), CancellationToken.None));
    }

    // ================================================================
    // Existing compatible embedding set
    // ================================================================

    [Fact]
    public async Task EnsureAsync_returns_SuccessExisting_when_compatible_set_exists()
    {
        var (service, _, snapshotReader, embeddingSets, _, _) = CreateService(MakeDocument());
        var snapshot = MakeSnapshot();
        snapshotReader.GetByDocumentResult = snapshot;
        var existing = MakeExistingEmbeddingSet();
        embeddingSets.GetByDocumentAndProfileResult = existing;

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentEmbeddingsStatus.SuccessExisting, result.Status);
        Assert.Same(existing, result.EmbeddingSet);
        Assert.False(embeddingSets.AddIfAbsentCalled);
    }

    // ================================================================
    // Existing incompatible embedding set (InvariantConflict)
    // ================================================================

    [Fact]
    public async Task EnsureAsync_returns_InvariantConflict_when_model_id_differs()
    {
        var (service, _, snapshotReader, embeddingSets, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot();
        embeddingSets.GetByDocumentAndProfileResult = MakeExistingEmbeddingSet(modelId: "different-model");

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentEmbeddingsStatus.InvariantConflict, result.Status);
    }

    [Fact]
    public async Task EnsureAsync_returns_InvariantConflict_when_dimensions_differ()
    {
        var (service, _, snapshotReader, embeddingSets, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot();
        embeddingSets.GetByDocumentAndProfileResult = MakeExistingEmbeddingSet(dimensions: 768);

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentEmbeddingsStatus.InvariantConflict, result.Status);
    }

    [Fact]
    public async Task EnsureAsync_returns_InvariantConflict_when_chunking_version_differs()
    {
        var (service, _, snapshotReader, embeddingSets, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot();
        embeddingSets.GetByDocumentAndProfileResult = MakeExistingEmbeddingSet(chunkingVersion: 99);

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentEmbeddingsStatus.InvariantConflict, result.Status);
    }

    [Fact]
    public async Task EnsureAsync_returns_InvariantConflict_when_embedding_count_differs()
    {
        var (service, _, snapshotReader, embeddingSets, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot();
        embeddingSets.GetByDocumentAndProfileResult = MakeExistingEmbeddingSet(embeddingCount: 99);

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentEmbeddingsStatus.InvariantConflict, result.Status);
    }

    // ================================================================
    // Happy path — create new embedding set
    // ================================================================

    [Fact]
    public async Task EnsureAsync_creates_embedding_set_on_happy_path()
    {
        var (service, _, snapshotReader, embeddingSets, generator, _) = CreateService(MakeDocument());
        var snapshot = MakeSnapshot(2);
        snapshotReader.GetByDocumentResult = snapshot;

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentEmbeddingsStatus.SuccessCreated, result.Status);
        Assert.True(embeddingSets.AddIfAbsentCalled);
        Assert.True(generator.GenerateCalled);

        var addedSet = embeddingSets.LastAddedEmbeddingSet!;
        Assert.Equal(DocumentId, addedSet.DocumentId);
        Assert.Equal(EmbeddingProfiles.SemanticV1Id, addedSet.ProfileId);
        Assert.Equal("text-embedding-3-small", addedSet.ModelId);
        Assert.Equal(EmbeddingProfiles.SemanticV1Dimensions, addedSet.Dimensions);
        Assert.Equal(2, addedSet.EmbeddingCount);
        Assert.Equal(Now, addedSet.CreatedAt);
    }

    [Fact]
    public async Task EnsureAsync_sends_chunk_texts_to_generator()
    {
        var (service, _, snapshotReader, _, generator, _) = CreateService(MakeDocument());
        var snapshot = MakeSnapshot(2);
        snapshotReader.GetByDocumentResult = snapshot;

        await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.NotNull(generator.ReceivedTexts);
        Assert.Equal(2, generator.ReceivedTexts.Count);
        Assert.Equal("Chunk text 0", generator.ReceivedTexts[0]);
        Assert.Equal("Chunk text 1", generator.ReceivedTexts[1]);
    }

    [Fact]
    public async Task EnsureAsync_maps_chunk_ids_to_embedding_inputs()
    {
        var (service, _, snapshotReader, embeddingSets, _, _) = CreateService(MakeDocument());
        var snapshot = MakeSnapshot(2);
        snapshotReader.GetByDocumentResult = snapshot;

        await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        var inputs = embeddingSets.LastAddedEmbeddings!;
        Assert.Equal(2, inputs.Count);
        Assert.Equal(snapshot.Chunks[0].ChunkId, inputs[0].DocumentChunkId);
        Assert.Equal(snapshot.Chunks[1].ChunkId, inputs[1].DocumentChunkId);
    }

    [Fact]
    public async Task EnsureAsync_passes_tenant_ids_to_repository()
    {
        var (service, _, snapshotReader, embeddingSets, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot(1);

        await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(ProjectId, embeddingSets.ReceivedAddProjectId);
        Assert.Equal(OrgId, embeddingSets.ReceivedAddOrganizationId);
    }

    // ================================================================
    // Zero-chunk document skips generator
    // ================================================================

    [Fact]
    public async Task EnsureAsync_skips_generator_when_zero_chunks()
    {
        var (service, _, snapshotReader, embeddingSets, generator, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot(0);

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentEmbeddingsStatus.SuccessCreated, result.Status);
        Assert.False(generator.GenerateCalled);
        Assert.True(embeddingSets.AddIfAbsentCalled);
        Assert.Empty(embeddingSets.LastAddedEmbeddings!);
        Assert.Equal(0, embeddingSets.LastAddedEmbeddingSet!.EmbeddingCount);
    }

    // ================================================================
    // Generator output validation
    // ================================================================

    [Fact]
    public async Task EnsureAsync_throws_when_generator_returns_wrong_count()
    {
        var (service, _, snapshotReader, _, generator, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot(2);
        generator.GenerateResult = [new float[1536]];

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureAsync(MakeCommand(), CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAsync_throws_when_generator_returns_wrong_dimensions()
    {
        var (service, _, snapshotReader, _, generator, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot(1);
        generator.GenerateResult = [new float[768]];

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureAsync(MakeCommand(), CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAsync_throws_when_vector_contains_nan()
    {
        var (service, _, snapshotReader, _, generator, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot(1);
        var badVector = new float[1536];
        badVector[0] = float.NaN;
        generator.GenerateResult = [badVector];

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureAsync(MakeCommand(), CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAsync_throws_when_vector_contains_infinity()
    {
        var (service, _, snapshotReader, _, generator, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot(1);
        var badVector = new float[1536];
        badVector[0] = float.PositiveInfinity;
        generator.GenerateResult = [badVector];

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureAsync(MakeCommand(), CancellationToken.None));
    }

    // ================================================================
    // Repository AddIfAbsent returns NotFound (tenant mismatch)
    // ================================================================

    [Fact]
    public async Task EnsureAsync_returns_NotFound_when_repo_add_returns_NotFound()
    {
        var (service, _, snapshotReader, embeddingSets, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot(1);
        embeddingSets.AddIfAbsentResult = DocumentEmbeddingSetAddResult.NotFound();

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentEmbeddingsStatus.NotFound, result.Status);
    }

    // ================================================================
    // Concurrent duplicate — AlreadyExists compatible
    // ================================================================

    [Fact]
    public async Task EnsureAsync_returns_SuccessExisting_when_concurrent_compatible_duplicate()
    {
        var (service, _, snapshotReader, embeddingSets, _, _) = CreateService(MakeDocument());
        var snapshot = MakeSnapshot(3);
        snapshotReader.GetByDocumentResult = snapshot;
        var winner = MakeExistingEmbeddingSet();
        embeddingSets.AddIfAbsentResult = DocumentEmbeddingSetAddResult.AlreadyExists(winner);

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentEmbeddingsStatus.SuccessExisting, result.Status);
        Assert.Same(winner, result.EmbeddingSet);
    }

    // ================================================================
    // Concurrent duplicate — AlreadyExists incompatible
    // ================================================================

    [Fact]
    public async Task EnsureAsync_returns_InvariantConflict_when_concurrent_incompatible_duplicate()
    {
        var (service, _, snapshotReader, embeddingSets, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot(3);
        var winner = MakeExistingEmbeddingSet(modelId: "wrong-model");
        embeddingSets.AddIfAbsentResult = DocumentEmbeddingSetAddResult.AlreadyExists(winner);

        var result = await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EnsureDocumentEmbeddingsStatus.InvariantConflict, result.Status);
        Assert.Same(winner, result.EmbeddingSet);
    }

    // ================================================================
    // Tenant isolation — IDs passed through correctly
    // ================================================================

    [Fact]
    public async Task EnsureAsync_passes_tenant_ids_to_document_repository()
    {
        var (service, documents, _, _, _, _) = CreateService(MakeDocument());

        await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(DocumentId, documents.ReceivedGetDocumentId);
        Assert.Equal(ProjectId, documents.ReceivedGetProjectId);
        Assert.Equal(OrgId, documents.ReceivedGetOrganizationId);
    }

    [Fact]
    public async Task EnsureAsync_passes_tenant_ids_to_snapshot_reader()
    {
        var (service, _, snapshotReader, _, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot(1);

        await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(DocumentId, snapshotReader.ReceivedDocumentId);
        Assert.Equal(ProjectId, snapshotReader.ReceivedProjectId);
        Assert.Equal(OrgId, snapshotReader.ReceivedOrganizationId);
    }

    [Fact]
    public async Task EnsureAsync_passes_profile_id_to_existing_check()
    {
        var (service, _, snapshotReader, embeddingSets, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot(1);

        await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(EmbeddingProfiles.SemanticV1Id, embeddingSets.ReceivedGetProfileId);
    }

    // ================================================================
    // EmbeddingSet constructed correctly
    // ================================================================

    [Fact]
    public async Task EnsureAsync_sets_chunking_version_from_snapshot()
    {
        var (service, _, snapshotReader, embeddingSets, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot(1);

        await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(1, embeddingSets.LastAddedEmbeddingSet!.ChunkingVersion);
    }

    [Fact]
    public async Task EnsureAsync_uses_clock_for_created_at()
    {
        var (service, _, snapshotReader, embeddingSets, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot(1);

        await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.Equal(Now, embeddingSets.LastAddedEmbeddingSet!.CreatedAt);
    }

    [Fact]
    public async Task EnsureAsync_generates_non_empty_id_for_set()
    {
        var (service, _, snapshotReader, embeddingSets, _, _) = CreateService(MakeDocument());
        snapshotReader.GetByDocumentResult = MakeSnapshot(1);

        await service.EnsureAsync(MakeCommand(), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, embeddingSets.LastAddedEmbeddingSet!.Id);
    }
}
