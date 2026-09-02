using OpsFlow.Application.Documents;
using OpsFlow.Application.UnitTests.TestSupport;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class SearchDocumentChunksLexicallyServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static (
        SearchDocumentChunksLexicallyService Service,
        FakeProjectRepository Projects,
        FakeLexicalChunkRetriever Retriever)
        CreateService()
    {
        var projects = new FakeProjectRepository();
        var retriever = new FakeLexicalChunkRetriever();
        var service = new SearchDocumentChunksLexicallyService(projects, retriever);
        return (service, projects, retriever);
    }

    private static SearchDocumentChunksLexicallyQuery MakeQuery(
        Guid? orgId = null,
        Guid? projectId = null,
        string queryText = "lexical search query",
        int topK = 10) =>
        new(orgId ?? OrgId, projectId ?? ProjectId, queryText, topK);

    // ================================================================
    // Constructor guards
    // ================================================================

    [Fact]
    public void Constructor_rejects_null_project_repository()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SearchDocumentChunksLexicallyService(null!, new FakeLexicalChunkRetriever()));
    }

    [Fact]
    public void Constructor_rejects_null_retriever()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SearchDocumentChunksLexicallyService(new FakeProjectRepository(), null!));
    }

    // ================================================================
    // Input validation
    // ================================================================

    [Fact]
    public async Task Search_rejects_null_query()
    {
        var (service, _, _) = CreateService();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.SearchAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_empty_organization_id()
    {
        var (service, _, _) = CreateService();
        var query = MakeQuery(orgId: Guid.Empty);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("Organization ID", ex.Message);
    }

    [Fact]
    public async Task Search_returns_project_not_found_for_empty_project_id()
    {
        var (service, _, _) = CreateService();
        var query = MakeQuery(projectId: Guid.Empty);

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.False(result.ProjectFound);
    }

    [Fact]
    public async Task Search_rejects_null_query_text()
    {
        var (service, _, _) = CreateService();
        var query = MakeQuery(queryText: null!);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_empty_query_text()
    {
        var (service, _, _) = CreateService();
        var query = MakeQuery(queryText: "");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("empty or whitespace", ex.Message);
    }

    [Fact]
    public async Task Search_rejects_whitespace_query_text()
    {
        var (service, _, _) = CreateService();
        var query = MakeQuery(queryText: "   \t\n");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("empty or whitespace", ex.Message);
    }

    [Fact]
    public async Task Search_accepts_query_text_at_max_length()
    {
        var (service, projects, _) = CreateService();
        projects.ExistsResult = true;

        var query = MakeQuery(queryText: new string('a', 2500));

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.True(result.ProjectFound);
    }

    [Fact]
    public async Task Search_rejects_query_text_exceeding_max_length()
    {
        var (service, _, _) = CreateService();
        var query = MakeQuery(queryText: new string('a', 2501));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("2500", ex.Message);
    }

    // ================================================================
    // Punctuation-only validation
    // ================================================================

    [Fact]
    public async Task Search_rejects_punctuation_only_query()
    {
        var (service, _, _) = CreateService();
        var query = MakeQuery(queryText: "!!!");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("letter or digit", ex.Message);
    }

    [Fact]
    public async Task Search_rejects_ellipsis_only_query()
    {
        var (service, _, _) = CreateService();
        var query = MakeQuery(queryText: "...");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(query, CancellationToken.None));
        Assert.Contains("letter or digit", ex.Message);
    }

    [Theory]
    [InlineData("C#")]
    [InlineData(".NET")]
    [InlineData("C++")]
    [InlineData("OAuth2")]
    public async Task Search_accepts_technical_terms_with_special_characters(string term)
    {
        var (service, projects, _) = CreateService();
        projects.ExistsResult = true;

        var query = MakeQuery(queryText: term);

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.True(result.ProjectFound);
    }

    // ================================================================
    // TopK validation
    // ================================================================

    [Fact]
    public async Task Search_rejects_topk_zero()
    {
        var (service, _, _) = CreateService();
        var query = MakeQuery(topK: 0);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_topk_negative()
    {
        var (service, _, _) = CreateService();
        var query = MakeQuery(topK: -1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_topk_above_maximum()
    {
        var (service, _, _) = CreateService();
        var query = MakeQuery(topK: 51);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Search_accepts_topk_one()
    {
        var (service, projects, _) = CreateService();
        projects.ExistsResult = true;

        var query = MakeQuery(topK: 1);

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.True(result.ProjectFound);
    }

    [Fact]
    public async Task Search_accepts_topk_fifty()
    {
        var (service, projects, _) = CreateService();
        projects.ExistsResult = true;

        var query = MakeQuery(topK: 50);

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.True(result.ProjectFound);
    }

    // ================================================================
    // Project not found — retriever not called
    // ================================================================

    [Fact]
    public async Task Search_returns_project_not_found_when_project_absent()
    {
        var (service, projects, retriever) = CreateService();
        projects.ExistsResult = false;

        var query = MakeQuery();

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.False(result.ProjectFound);
        Assert.False(retriever.RetrieveCalled);
    }

    [Fact]
    public async Task Search_returns_project_not_found_indistinguishable_from_cross_tenant()
    {
        var (service, projects, _) = CreateService();
        projects.ExistsResult = false;

        var crossTenantQuery = MakeQuery(orgId: Guid.NewGuid());

        var result = await service.SearchAsync(crossTenantQuery, CancellationToken.None);

        Assert.False(result.ProjectFound);
    }

    // ================================================================
    // Correct forwarding
    // ================================================================

    [Fact]
    public async Task Search_calls_retriever_with_correct_parameters()
    {
        var (service, projects, retriever) = CreateService();
        projects.ExistsResult = true;

        var query = MakeQuery(topK: 5);

        await service.SearchAsync(query, CancellationToken.None);

        Assert.True(retriever.RetrieveCalled);
        Assert.Equal(OrgId, retriever.ReceivedOrganizationId);
        Assert.Equal(ProjectId, retriever.ReceivedProjectId);
        Assert.Equal("lexical search query", retriever.ReceivedQueryText);
        Assert.Equal(5, retriever.ReceivedTopK);
    }

    [Fact]
    public async Task Search_returns_hits_in_retriever_order()
    {
        var (service, projects, retriever) = CreateService();
        projects.ExistsResult = true;

        var hit1 = new LexicalChunkHit(Guid.NewGuid(), Guid.NewGuid(), 0, 0, 5, "hello", 100);
        var hit2 = new LexicalChunkHit(Guid.NewGuid(), Guid.NewGuid(), 1, 5, 10, "world", 50);
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
        var (service, projects, retriever) = CreateService();
        projects.ExistsResult = true;
        retriever.RetrieveResult = [];

        var query = MakeQuery();

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.True(result.ProjectFound);
        Assert.Empty(result.Hits);
    }
}
