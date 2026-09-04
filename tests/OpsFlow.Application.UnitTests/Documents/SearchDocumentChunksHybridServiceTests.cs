using OpsFlow.Application.Documents;
using OpsFlow.Application.UnitTests.TestSupport;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class SearchDocumentChunksHybridServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static (
        SearchDocumentChunksHybridService Service,
        FakeProjectRepository Projects,
        FakeEmbeddingGenerator Generator,
        FakeSemanticChunkRetriever SemanticRetriever,
        FakeLexicalChunkRetriever LexicalRetriever)
        CreateService()
    {
        var projects = new FakeProjectRepository();
        var generator = new FakeEmbeddingGenerator();
        var semanticRetriever = new FakeSemanticChunkRetriever();
        var lexicalRetriever = new FakeLexicalChunkRetriever();
        var service = new SearchDocumentChunksHybridService(
            projects, generator, semanticRetriever, lexicalRetriever);
        return (service, projects, generator, semanticRetriever, lexicalRetriever);
    }

    private static SearchDocumentChunksHybridQuery MakeQuery(
        Guid? orgId = null,
        Guid? projectId = null,
        string queryText = "hybrid search query",
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
            new SearchDocumentChunksHybridService(
                null!, new FakeEmbeddingGenerator(),
                new FakeSemanticChunkRetriever(), new FakeLexicalChunkRetriever()));
    }

    [Fact]
    public void Constructor_rejects_null_generator()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SearchDocumentChunksHybridService(
                new FakeProjectRepository(), null!,
                new FakeSemanticChunkRetriever(), new FakeLexicalChunkRetriever()));
    }

    [Fact]
    public void Constructor_rejects_null_semantic_retriever()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SearchDocumentChunksHybridService(
                new FakeProjectRepository(), new FakeEmbeddingGenerator(),
                null!, new FakeLexicalChunkRetriever()));
    }

    [Fact]
    public void Constructor_rejects_null_lexical_retriever()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SearchDocumentChunksHybridService(
                new FakeProjectRepository(), new FakeEmbeddingGenerator(),
                new FakeSemanticChunkRetriever(), null!));
    }

    // ================================================================
    // Input validation
    // ================================================================

    [Fact]
    public async Task Search_rejects_null_query()
    {
        var (service, _, _, _, _) = CreateService();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.SearchAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_empty_organization_id()
    {
        var (service, _, _, _, _) = CreateService();
        var query = MakeQuery(orgId: Guid.Empty);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("Organization ID", ex.Message);
    }

    [Fact]
    public async Task Search_returns_project_not_found_for_empty_project_id()
    {
        var (service, _, _, _, _) = CreateService();
        var query = MakeQuery(projectId: Guid.Empty);

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.False(result.ProjectFound);
    }

    [Fact]
    public async Task Search_rejects_null_query_text()
    {
        var (service, _, _, _, _) = CreateService();
        var query = MakeQuery(queryText: null!);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_empty_query_text()
    {
        var (service, _, _, _, _) = CreateService();
        var query = MakeQuery(queryText: "");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("empty or whitespace", ex.Message);
    }

    [Fact]
    public async Task Search_rejects_whitespace_query_text()
    {
        var (service, _, _, _, _) = CreateService();
        var query = MakeQuery(queryText: "   \t\n");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("empty or whitespace", ex.Message);
    }

    [Fact]
    public async Task Search_rejects_punctuation_only_query()
    {
        var (service, _, _, _, _) = CreateService();
        var query = MakeQuery(queryText: "!!!");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("letter or digit", ex.Message);
    }

    [Fact]
    public async Task Search_accepts_supplementary_plane_unicode_letter()
    {
        var (service, projects, generator, _, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var supplementaryLetter = char.ConvertFromUtf32(0x10400);
        var query = MakeQuery(queryText: supplementaryLetter);

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.True(result.ProjectFound);
    }

    [Fact]
    public async Task Search_accepts_query_text_at_max_length()
    {
        var (service, projects, generator, _, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var query = MakeQuery(queryText: new string('a', 2500));

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.True(result.ProjectFound);
    }

    [Fact]
    public async Task Search_rejects_query_text_exceeding_max_length()
    {
        var (service, _, _, _, _) = CreateService();
        var query = MakeQuery(queryText: new string('a', 2501));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("2500", ex.Message);
    }

    [Fact]
    public async Task Search_rejects_topk_zero()
    {
        var (service, _, _, _, _) = CreateService();
        var query = MakeQuery(topK: 0);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_topk_negative()
    {
        var (service, _, _, _, _) = CreateService();
        var query = MakeQuery(topK: -1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_topk_above_maximum()
    {
        var (service, _, _, _, _) = CreateService();
        var query = MakeQuery(topK: 51);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_accepts_topk_one()
    {
        var (service, projects, generator, _, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var query = MakeQuery(topK: 1);

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.True(result.ProjectFound);
    }

    [Fact]
    public async Task Search_accepts_topk_fifty()
    {
        var (service, projects, generator, _, _) = CreateService();
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
        var (service, _, generator, _, _) = CreateService();
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
        var (service, _, generator, _, _) = CreateService();
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
        var (service, _, generator, _, _) = CreateService();
        generator.Identity = new EmbeddingGeneratorIdentity(
            EmbeddingProfiles.SemanticV1Id, EmbeddingProfiles.SemanticV1ModelId, 768);

        var query = MakeQuery();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("dimensions", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(generator.GenerateCalled);
    }

    // ================================================================
    // Project not found
    // ================================================================

    [Fact]
    public async Task Search_returns_project_not_found_when_project_absent()
    {
        var (service, projects, generator, semanticRetriever, lexicalRetriever) = CreateService();
        projects.ExistsResult = false;

        var query = MakeQuery();

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.False(result.ProjectFound);
        Assert.False(generator.GenerateCalled);
        Assert.False(semanticRetriever.RetrieveCalled);
        Assert.False(lexicalRetriever.RetrieveCalled);
    }

    [Fact]
    public async Task Search_returns_project_not_found_indistinguishable_from_cross_tenant()
    {
        var (service, projects, _, _, _) = CreateService();
        projects.ExistsResult = false;

        var crossTenantQuery = MakeQuery(orgId: Guid.NewGuid());

        var result = await service.SearchAsync(crossTenantQuery, CancellationToken.None);

        Assert.False(result.ProjectFound);
    }

    [Fact]
    public async Task Search_checks_project_existence_once()
    {
        var (service, projects, _, _, _) = CreateService();
        projects.ExistsResult = false;

        var query = MakeQuery();
        await service.SearchAsync(query, CancellationToken.None);

        Assert.Equal(ProjectId, projects.ReceivedExistsProjectId);
        Assert.Equal(OrgId, projects.ReceivedExistsOrganizationId);
    }

    // ================================================================
    // Generator output validation
    // ================================================================

    [Fact]
    public async Task Search_rejects_generator_returning_zero_vectors()
    {
        var (service, projects, generator, _, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [];

        var query = MakeQuery();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_generator_returning_multiple_vectors()
    {
        var (service, projects, generator, _, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector(), MakeVector()];

        var query = MakeQuery();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_generator_returning_wrong_dimensions()
    {
        var (service, projects, generator, _, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [new float[768]];

        var query = MakeQuery();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_generator_returning_NaN()
    {
        var (service, projects, generator, _, _) = CreateService();
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
        var (service, projects, generator, _, _) = CreateService();
        projects.ExistsResult = true;
        var vec = MakeVector();
        vec[0] = float.PositiveInfinity;
        generator.GenerateResult = [vec];

        var query = MakeQuery();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_generator_returning_zero_norm_vector()
    {
        var (service, projects, generator, semanticRetriever, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [new float[EmbeddingProfiles.SemanticV1Dimensions]];

        var query = MakeQuery();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync(query, CancellationToken.None));

        Assert.False(semanticRetriever.RetrieveCalled);
    }

    // ================================================================
    // Forwarding — generator
    // ================================================================

    [Fact]
    public async Task Search_calls_generator_exactly_once_with_original_query_text()
    {
        var (service, projects, generator, _, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var query = MakeQuery(queryText: "  find relevant chunks  ");

        await service.SearchAsync(query, CancellationToken.None);

        Assert.True(generator.GenerateCalled);
        Assert.NotNull(generator.ReceivedTexts);
        Assert.Single(generator.ReceivedTexts);
        Assert.Equal("  find relevant chunks  ", generator.ReceivedTexts[0]);
    }

    // ================================================================
    // Forwarding — semantic retriever
    // ================================================================

    [Fact]
    public async Task Search_calls_semantic_retriever_with_correct_parameters()
    {
        var (service, projects, generator, semanticRetriever, _) = CreateService();
        projects.ExistsResult = true;
        var expectedVector = MakeVector();
        generator.GenerateResult = [expectedVector];

        var query = MakeQuery(topK: 5);

        await service.SearchAsync(query, CancellationToken.None);

        Assert.True(semanticRetriever.RetrieveCalled);
        Assert.Equal(OrgId, semanticRetriever.ReceivedOrganizationId);
        Assert.Equal(ProjectId, semanticRetriever.ReceivedProjectId);
        Assert.Equal(generator.Identity, semanticRetriever.ReceivedIdentity);
        Assert.Equal(50, semanticRetriever.ReceivedTopK);
        Assert.NotNull(semanticRetriever.ReceivedQueryEmbedding);
        Assert.Equal(expectedVector.Length, semanticRetriever.ReceivedQueryEmbedding.Value.Length);
    }

    // ================================================================
    // Forwarding — lexical retriever
    // ================================================================

    [Fact]
    public async Task Search_calls_lexical_retriever_with_correct_parameters()
    {
        var (service, projects, generator, _, lexicalRetriever) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var query = MakeQuery(topK: 5);

        await service.SearchAsync(query, CancellationToken.None);

        Assert.True(lexicalRetriever.RetrieveCalled);
        Assert.Equal(OrgId, lexicalRetriever.ReceivedOrganizationId);
        Assert.Equal(ProjectId, lexicalRetriever.ReceivedProjectId);
        Assert.Equal("hybrid search query", lexicalRetriever.ReceivedQueryText);
        Assert.Equal(50, lexicalRetriever.ReceivedTopK);
    }

    // ================================================================
    // Result semantics
    // ================================================================

    [Fact]
    public async Task Search_returns_empty_hits_when_both_retrievers_return_empty()
    {
        var (service, projects, generator, _, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var query = MakeQuery();

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.True(result.ProjectFound);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task Search_returns_semantic_only_results()
    {
        var (service, projects, generator, semanticRetriever, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var chunkId = Guid.NewGuid();
        semanticRetriever.RetrieveResult =
            [new SemanticChunkHit(Guid.NewGuid(), chunkId, 0, 0, 5, "hello", 0.1)];

        var query = MakeQuery();

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.Single(result.Hits);
        Assert.Equal(chunkId, result.Hits[0].DocumentChunkId);
        Assert.Equal(1, result.Hits[0].SemanticRank);
        Assert.Null(result.Hits[0].LexicalRank);
    }

    [Fact]
    public async Task Search_returns_lexical_only_results()
    {
        var (service, projects, generator, _, lexicalRetriever) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var chunkId = Guid.NewGuid();
        lexicalRetriever.RetrieveResult =
            [new LexicalChunkHit(Guid.NewGuid(), chunkId, 0, 0, 5, "hello", 100)];

        var query = MakeQuery();

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.Single(result.Hits);
        Assert.Equal(chunkId, result.Hits[0].DocumentChunkId);
        Assert.Null(result.Hits[0].SemanticRank);
        Assert.Equal(1, result.Hits[0].LexicalRank);
    }

    [Fact]
    public async Task Search_fuses_overlapping_chunk()
    {
        var (service, projects, generator, semanticRetriever, lexicalRetriever) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var docId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();
        semanticRetriever.RetrieveResult =
            [new SemanticChunkHit(docId, chunkId, 0, 0, 5, "hello", 0.1)];
        lexicalRetriever.RetrieveResult =
            [new LexicalChunkHit(docId, chunkId, 0, 0, 5, "hello", 100)];

        var query = MakeQuery();

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.Single(result.Hits);
        Assert.Equal(chunkId, result.Hits[0].DocumentChunkId);
        Assert.Equal(1, result.Hits[0].SemanticRank);
        Assert.Equal(1, result.Hits[0].LexicalRank);
        Assert.Equal((1.0 / (60 + 1)) + (1.0 / (60 + 1)), result.Hits[0].RrfScore);
    }

    [Fact]
    public async Task Search_honors_final_topk()
    {
        var (service, projects, generator, semanticRetriever, _) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        semanticRetriever.RetrieveResult =
            [.. Enumerable.Range(0, 10)
                .Select(i => new SemanticChunkHit(
                    Guid.NewGuid(), Guid.NewGuid(), i, i * 5, (i * 5) + 5, $"chunk{i}", 0.1 * (i + 1)))];

        var query = MakeQuery(topK: 3);

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.Equal(3, result.Hits.Count);
    }

    [Fact]
    public async Task Search_returns_hits_in_rrf_order()
    {
        var (service, projects, generator, semanticRetriever, lexicalRetriever) = CreateService();
        projects.ExistsResult = true;
        generator.GenerateResult = [MakeVector()];

        var docId = Guid.NewGuid();
        var overlapChunkId = Guid.NewGuid();
        var semanticOnlyChunkId = Guid.NewGuid();

        semanticRetriever.RetrieveResult =
        [
            new SemanticChunkHit(docId, overlapChunkId, 0, 0, 5, "hello", 0.1),
            new SemanticChunkHit(docId, semanticOnlyChunkId, 1, 5, 10, "world", 0.5),
        ];
        lexicalRetriever.RetrieveResult =
        [
            new LexicalChunkHit(docId, overlapChunkId, 0, 0, 5, "hello", 100),
        ];

        var query = MakeQuery();

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.Equal(2, result.Hits.Count);
        Assert.Equal(overlapChunkId, result.Hits[0].DocumentChunkId);
        Assert.Equal(semanticOnlyChunkId, result.Hits[1].DocumentChunkId);
        Assert.True(result.Hits[0].RrfScore > result.Hits[1].RrfScore);
    }
}
