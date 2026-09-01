using OpsFlow.Application.Documents;
using OpsFlow.Application.UnitTests.TestSupport;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class SearchDocumentChunksServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static (
        SearchDocumentChunksService Service,
        FakeProjectRepository Projects,
        FakeEmbeddingGenerator Generator,
        FakeSemanticChunkRetriever Retriever)
        CreateService()
    {
        var projects = new FakeProjectRepository();
        var generator = new FakeEmbeddingGenerator();
        var retriever = new FakeSemanticChunkRetriever();
        var service = new SearchDocumentChunksService(projects, generator, retriever);
        return (service, projects, generator, retriever);
    }

    private static SearchDocumentChunksQuery MakeQuery(
        Guid? orgId = null,
        Guid? projectId = null,
        string queryText = "semantic search query",
        int topK = 10) =>
        new(orgId ?? OrgId, projectId ?? ProjectId, queryText, topK);

    private static float[] MakeVector(int dimensions = 1536)
    {
        var v = new float[dimensions];
        v[0] = 1.0f;
        return v;
    }

    // ================================================================
    // Constructor guards
    // ================================================================

    [Fact]
    public void Constructor_rejects_null_project_repository()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SearchDocumentChunksService(null!, new FakeEmbeddingGenerator(), new FakeSemanticChunkRetriever()));
    }

    [Fact]
    public void Constructor_rejects_null_generator()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SearchDocumentChunksService(new FakeProjectRepository(), null!, new FakeSemanticChunkRetriever()));
    }

    [Fact]
    public void Constructor_rejects_null_retriever()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SearchDocumentChunksService(new FakeProjectRepository(), new FakeEmbeddingGenerator(), null!));
    }

    // ================================================================
    // Input validation
    // ================================================================

    [Fact]
    public async Task Search_rejects_null_query()
    {
        var (service, _, _, _) = CreateService();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.SearchAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_empty_organization_id()
    {
        var (service, _, _, _) = CreateService();
        var query = MakeQuery(orgId: Guid.Empty);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("Organization ID", ex.Message);
    }

    [Fact]
    public async Task Search_returns_project_not_found_for_empty_project_id()
    {
        var (service, _, _, _) = CreateService();
        var query = MakeQuery(projectId: Guid.Empty);

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.False(result.ProjectFound);
    }

    [Fact]
    public async Task Search_rejects_null_query_text()
    {
        var (service, _, _, _) = CreateService();
        var query = MakeQuery(queryText: null!);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_empty_query_text()
    {
        var (service, _, _, _) = CreateService();
        var query = MakeQuery(queryText: "");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("empty or whitespace", ex.Message);
    }

    [Fact]
    public async Task Search_rejects_whitespace_query_text()
    {
        var (service, _, _, _) = CreateService();
        var query = MakeQuery(queryText: "   \t\n");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("empty or whitespace", ex.Message);
    }

    [Fact]
    public async Task Search_accepts_query_text_at_max_length()
    {
        var (service, projects, generator, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var query = MakeQuery(queryText: new string('a', 2500));

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.True(result.ProjectFound);
    }

    [Fact]
    public async Task Search_rejects_query_text_exceeding_max_length()
    {
        var (service, _, _, _) = CreateService();
        var query = MakeQuery(queryText: new string('a', 2501));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("2500", ex.Message);
    }

    [Fact]
    public async Task Search_rejects_topk_zero()
    {
        var (service, _, _, _) = CreateService();
        var query = MakeQuery(topK: 0);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_topk_negative()
    {
        var (service, _, _, _) = CreateService();
        var query = MakeQuery(topK: -1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_topk_above_maximum()
    {
        var (service, _, _, _) = CreateService();
        var query = MakeQuery(topK: 51);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_accepts_topk_one()
    {
        var (service, projects, generator, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var query = MakeQuery(topK: 1);

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.True(result.ProjectFound);
    }

    [Fact]
    public async Task Search_accepts_topk_fifty()
    {
        var (service, projects, generator, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var query = MakeQuery(topK: 50);

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.True(result.ProjectFound);
    }

    // ================================================================
    // Generator identity validation — BEFORE provider call
    // ================================================================

    [Fact]
    public async Task Search_rejects_wrong_profile_id_before_provider_call()
    {
        var (service, _, generator, _) = CreateService();
        generator.Identity = new EmbeddingGeneratorIdentity(
            "wrong-profile", EmbeddingProfiles.SemanticV1ModelId, EmbeddingProfiles.SemanticV1Dimensions);

        var query = MakeQuery();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("profile", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(generator.GenerateCalled);
    }

    [Fact]
    public async Task Search_rejects_wrong_model_id_before_provider_call()
    {
        var (service, _, generator, _) = CreateService();
        generator.Identity = new EmbeddingGeneratorIdentity(
            EmbeddingProfiles.SemanticV1Id, "wrong-model", EmbeddingProfiles.SemanticV1Dimensions);

        var query = MakeQuery();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("model", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(generator.GenerateCalled);
    }

    [Fact]
    public async Task Search_rejects_wrong_dimensions_before_provider_call()
    {
        var (service, _, generator, _) = CreateService();
        generator.Identity = new EmbeddingGeneratorIdentity(
            EmbeddingProfiles.SemanticV1Id, EmbeddingProfiles.SemanticV1ModelId, 768);

        var query = MakeQuery();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("dimensions", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(generator.GenerateCalled);
    }

    // ================================================================
    // Project not found — generator not called
    // ================================================================

    [Fact]
    public async Task Search_returns_project_not_found_when_project_absent()
    {
        var (service, projects, generator, _) = CreateService();
        projects.ExistsResult = false;

        var query = MakeQuery();

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.False(result.ProjectFound);
        Assert.False(generator.GenerateCalled);
    }

    // ================================================================
    // Generator output validation
    // ================================================================

    [Fact]
    public async Task Search_rejects_generator_returning_zero_vectors()
    {
        var (service, projects, generator, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [];

        var query = MakeQuery();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_generator_returning_multiple_vectors()
    {
        var (service, projects, generator, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector(), MakeVector()];

        var query = MakeQuery();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_generator_returning_wrong_dimensions()
    {
        var (service, projects, generator, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [new float[768]];

        var query = MakeQuery();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_generator_returning_NaN()
    {
        var (service, projects, generator, _) = CreateService();
        projects.ExistsResult = true;
        var vec = MakeVector();
        vec[0] = float.NaN;
        generator.GenerateResult = [vec];

        var query = MakeQuery();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_generator_returning_positive_infinity()
    {
        var (service, projects, generator, _) = CreateService();
        projects.ExistsResult = true;
        var vec = MakeVector();
        vec[0] = float.PositiveInfinity;
        generator.GenerateResult = [vec];

        var query = MakeQuery();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_generator_returning_negative_infinity()
    {
        var (service, projects, generator, _) = CreateService();
        projects.ExistsResult = true;
        var vec = MakeVector();
        vec[0] = float.NegativeInfinity;
        generator.GenerateResult = [vec];

        var query = MakeQuery();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    // ================================================================
    // Exception propagation
    // ================================================================

    [Fact]
    public async Task Search_propagates_embedding_generation_exception()
    {
        var (service, projects, _, _) = CreateService();
        projects.ExistsResult = true;

        var generator = new ThrowingEmbeddingGenerator(
            new EmbeddingGenerationException("provider error"));
        var throwingService = new SearchDocumentChunksService(
            projects, generator, new FakeSemanticChunkRetriever());

        var query = MakeQuery();

        var ex = await Assert.ThrowsAsync<EmbeddingGenerationException>(() =>
            throwingService.SearchAsync(query, CancellationToken.None));
        Assert.Equal("provider error", ex.Message);
    }

    [Fact]
    public async Task Search_propagates_cancellation()
    {
        var (service, projects, _, _) = CreateService();
        projects.ExistsResult = true;

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var generator = new ThrowingEmbeddingGenerator(
            new OperationCanceledException(cts.Token));
        var cancelService = new SearchDocumentChunksService(
            projects, generator, new FakeSemanticChunkRetriever());

        var query = MakeQuery();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            cancelService.SearchAsync(query, cts.Token));
    }

    // ================================================================
    // Correct forwarding
    // ================================================================

    [Fact]
    public async Task Search_calls_generator_exactly_once_with_original_query_text()
    {
        var (service, projects, generator, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var query = MakeQuery(queryText: "  find relevant chunks  ");

        await service.SearchAsync(query, CancellationToken.None);

        Assert.True(generator.GenerateCalled);
        Assert.NotNull(generator.ReceivedTexts);
        Assert.Single(generator.ReceivedTexts);
        Assert.Equal("  find relevant chunks  ", generator.ReceivedTexts[0]);
    }

    [Fact]
    public async Task Search_calls_retriever_with_correct_parameters()
    {
        var (service, projects, generator, retriever) = CreateService();
        projects.ExistsResult = true;
        var expectedVector = MakeVector();
        generator.GenerateResult = [expectedVector];

        var query = MakeQuery(topK: 5);

        await service.SearchAsync(query, CancellationToken.None);

        Assert.True(retriever.RetrieveCalled);
        Assert.Equal(OrgId, retriever.ReceivedOrganizationId);
        Assert.Equal(ProjectId, retriever.ReceivedProjectId);
        Assert.Equal(generator.Identity, retriever.ReceivedIdentity);
        Assert.Equal(5, retriever.ReceivedTopK);
        Assert.NotNull(retriever.ReceivedQueryEmbedding);
        Assert.Equal(expectedVector.Length, retriever.ReceivedQueryEmbedding.Value.Length);
    }

    [Fact]
    public async Task Search_returns_hits_in_retriever_order()
    {
        var (service, projects, generator, retriever) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var hit1 = new SemanticChunkHit(Guid.NewGuid(), Guid.NewGuid(), 0, 0, 5, "hello", 0.1);
        var hit2 = new SemanticChunkHit(Guid.NewGuid(), Guid.NewGuid(), 1, 5, 10, "world", 0.5);
        retriever.RetrieveResult = [hit1, hit2];

        var query = MakeQuery();

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.True(result.ProjectFound);
        Assert.Equal(2, result.Hits.Count);
        Assert.Same(hit1, result.Hits[0]);
        Assert.Same(hit2, result.Hits[1]);
    }

    [Fact]
    public async Task Search_returns_empty_hits_as_success()
    {
        var (service, projects, generator, retriever) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];
        retriever.RetrieveResult = [];

        var query = MakeQuery();

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.True(result.ProjectFound);
        Assert.Empty(result.Hits);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private sealed class ThrowingEmbeddingGenerator : IEmbeddingGenerator
    {
        private readonly Exception _exception;

        public ThrowingEmbeddingGenerator(Exception exception)
        {
            _exception = exception;
        }

        public EmbeddingGeneratorIdentity Identity { get; } = new(
            EmbeddingProfiles.SemanticV1Id,
            EmbeddingProfiles.SemanticV1ModelId,
            EmbeddingProfiles.SemanticV1Dimensions);

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }
}
