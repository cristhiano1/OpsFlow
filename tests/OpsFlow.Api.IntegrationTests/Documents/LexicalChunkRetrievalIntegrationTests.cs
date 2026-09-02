using Microsoft.EntityFrameworkCore;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;
using OpsFlow.Domain.Organizations;
using OpsFlow.Domain.Projects;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Api.IntegrationTests.Documents;

[Collection(SqlServerCollection.Name)]
public sealed class LexicalChunkRetrievalIntegrationTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public LexicalChunkRetrievalIntegrationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    // ================================================================
    // Helpers
    // ================================================================

    private async Task<(Guid OrgId, Guid ProjectId)> SeedTenantAsync()
    {
        await using var db = _fixture.CreateContext();
        var orgId = Guid.NewGuid();
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = "TestOrg-" + orgId.ToString("N")[..8],
            Slug = "test-org-" + orgId.ToString("N")[..8],
            CreatedAt = Timestamp,
        });
        await db.SaveChangesAsync();

        var projectId = Guid.NewGuid();
        db.Projects.Add(new Project(projectId, orgId, "TestProject", null, Timestamp));
        await db.SaveChangesAsync();

        return (orgId, projectId);
    }

    private async Task<SeedResult> SeedDocumentWithChunksAsync(
        Guid orgId,
        Guid projectId,
        string[] chunkTexts,
        Guid? documentId = null)
    {
        await using var db = _fixture.CreateContext();

        var docId = documentId ?? Guid.NewGuid();
        db.Documents.Add(new Document(docId, orgId, projectId, "test.txt", "text/plain", 100, Timestamp));
        await db.SaveChangesAsync();

        var fullText = string.Concat(chunkTexts);
        db.DocumentExtractions.Add(new DocumentExtraction(docId, fullText.Length > 0 ? fullText : "empty", Timestamp));
        await db.SaveChangesAsync();

        db.DocumentChunkSets.Add(new DocumentChunkSet(docId, 1, chunkTexts.Length, Timestamp));
        var chunkIds = new List<Guid>();
        var offset = 0;
        for (int i = 0; i < chunkTexts.Length; i++)
        {
            var text = chunkTexts[i];
            var chunkId = Guid.NewGuid();
            chunkIds.Add(chunkId);
            db.DocumentChunks.Add(new DocumentChunk(chunkId, docId, i, offset, offset + text.Length, text));
            offset += text.Length;
        }
        await db.SaveChangesAsync();

        return new SeedResult(docId, chunkIds);
    }

    private async Task WaitForFullTextPopulationAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var deadline = DateTime.UtcNow + effectiveTimeout;
        await using var db = _fixture.CreateContext();

        while (DateTime.UtcNow < deadline)
        {
            var status = await db.Database
                .SqlQuery<int>($"SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID('DocumentChunks'), 'TableFulltextPopulateStatus') AS int) AS [Value]")
                .SingleAsync();
            var pending = await db.Database
                .SqlQuery<int>($"SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID('DocumentChunks'), 'TableFulltextPendingChanges') AS int) AS [Value]")
                .SingleAsync();

            if (status == 0 && pending == 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Full-text population on DocumentChunks did not complete within {effectiveTimeout.TotalSeconds}s.");
    }

    private async Task<IReadOnlyList<LexicalChunkHit>> RetrieveAsync(
        Guid orgId, Guid projectId, string queryText, int topK = 10)
    {
        await using var db = _fixture.CreateContext();
        var retriever = new EfLexicalChunkRetriever(db);
        return await retriever.RetrieveAsync(orgId, projectId, queryText, topK, CancellationToken.None);
    }

    private sealed record SeedResult(Guid DocumentId, List<Guid> ChunkIds);

    // ================================================================
    // 1. FTS installed
    // ================================================================

    [Fact]
    public async Task FullTextSearch_is_installed()
    {
        await using var db = _fixture.CreateContext();

        var result = await db.Database
            .SqlQuery<int>($"SELECT CAST(SERVERPROPERTY('IsFullTextInstalled') AS int) AS [Value]")
            .SingleAsync();

        Assert.Equal(1, result);
    }

    // ================================================================
    // 2. Lexical matching works
    // ================================================================

    [Fact]
    public async Task Retrieval_returns_chunks_matching_search_terms()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithChunksAsync(orgId, projectId,
            ["The deployment pipeline uses Kubernetes orchestration for container management"]);
        await WaitForFullTextPopulationAsync();

        var hits = await RetrieveAsync(orgId, projectId, "Kubernetes deployment");

        Assert.NotEmpty(hits);
        Assert.Contains("Kubernetes", hits[0].Text);
    }

    // ================================================================
    // 3. FtsRank DESC ordering
    // ================================================================

    [Fact]
    public async Task Retrieval_orders_by_fts_rank_descending()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithChunksAsync(orgId, projectId,
        [
            "Machine learning algorithms optimize neural network training processes",
            "The weather forecast predicts sunny skies for the upcoming weekend",
            "Deep learning and machine learning drive modern artificial intelligence",
        ]);
        await WaitForFullTextPopulationAsync();

        var hits = await RetrieveAsync(orgId, projectId, "machine learning algorithms");

        Assert.True(hits.Count >= 2);
        for (int i = 1; i < hits.Count; i++)
        {
            Assert.True(hits[i - 1].FtsRank >= hits[i].FtsRank,
                $"Expected rank[{i - 1}]={hits[i - 1].FtsRank} >= rank[{i}]={hits[i].FtsRank}");
        }
    }

    // ================================================================
    // 4. Organization isolation
    // ================================================================

    [Fact]
    public async Task Retrieval_excludes_chunks_from_other_organization()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithChunksAsync(orgId, projectId,
            ["Quantum computing research advances cryptographic security protocols"]);
        await WaitForFullTextPopulationAsync();

        var (otherOrgId, _) = await SeedTenantAsync();

        var hits = await RetrieveAsync(otherOrgId, projectId, "quantum computing");

        Assert.Empty(hits);
    }

    // ================================================================
    // 5. Project isolation
    // ================================================================

    [Fact]
    public async Task Retrieval_excludes_chunks_from_other_project()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithChunksAsync(orgId, projectId,
            ["Blockchain technology enables decentralized financial transactions"]);
        await WaitForFullTextPopulationAsync();

        await using var db = _fixture.CreateContext();
        var otherProjectId = Guid.NewGuid();
        db.Projects.Add(new Project(otherProjectId, orgId, "OtherProject", null, Timestamp));
        await db.SaveChangesAsync();

        var hits = await RetrieveAsync(orgId, otherProjectId, "blockchain technology");

        Assert.Empty(hits);
    }

    // ================================================================
    // 6. TopK server-side
    // ================================================================

    [Fact]
    public async Task Retrieval_limits_results_to_topk()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithChunksAsync(orgId, projectId,
        [
            "Software engineering practices improve code quality and reliability",
            "Software testing frameworks validate software engineering standards",
            "Software architecture patterns guide software engineering decisions",
            "Software deployment automation streamlines software delivery cycles",
            "Software monitoring tools track software performance and uptime",
        ]);
        await WaitForFullTextPopulationAsync();

        var hits = await RetrieveAsync(orgId, projectId, "software engineering", topK: 3);

        Assert.Equal(3, hits.Count);
    }

    // ================================================================
    // 7. Deterministic equal-rank ordering
    // ================================================================

    [Fact]
    public async Task Retrieval_orders_by_document_id_then_chunk_index_on_rank_tie()
    {
        var (orgId, projectId) = await SeedTenantAsync();

        // Distinct fixed GUIDs (…00A1/…00A2) that do not collide with other
        // tests in this shared-database collection. SQL Server orders
        // uniqueidentifier by the last 6 bytes first, so …00A1 < …00A2.
        var sqlLowerId = new Guid("00000000-0000-0000-0000-0000000000A1");
        var sqlHigherId = new Guid("00000000-0000-0000-0000-0000000000A2");

        await SeedDocumentWithChunksAsync(orgId, projectId,
            ["Database indexing optimizes query execution performance significantly",
             "Database indexing optimizes query execution performance significantly"],
            documentId: sqlHigherId);
        await SeedDocumentWithChunksAsync(orgId, projectId,
            ["Database indexing optimizes query execution performance significantly",
             "Database indexing optimizes query execution performance significantly"],
            documentId: sqlLowerId);
        await WaitForFullTextPopulationAsync();

        var hits = await RetrieveAsync(orgId, projectId, "database indexing query");

        Assert.Equal(4, hits.Count);

        Assert.Equal(sqlLowerId, hits[0].DocumentId);
        Assert.Equal(0, hits[0].ChunkIndex);
        Assert.Equal(sqlLowerId, hits[1].DocumentId);
        Assert.Equal(1, hits[1].ChunkIndex);
        Assert.Equal(sqlHigherId, hits[2].DocumentId);
        Assert.Equal(0, hits[2].ChunkIndex);
        Assert.Equal(sqlHigherId, hits[3].DocumentId);
        Assert.Equal(1, hits[3].ChunkIndex);
    }

    // ================================================================
    // 8. Exact persisted metadata
    // ================================================================

    [Fact]
    public async Task Retrieval_returns_exact_chunk_metadata()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        var seed = await SeedDocumentWithChunksAsync(orgId, projectId,
            ["Infrastructure monitoring provides observability into system health"]);
        await WaitForFullTextPopulationAsync();

        var hits = await RetrieveAsync(orgId, projectId, "infrastructure monitoring observability");

        Assert.Single(hits);
        var hit = hits[0];
        Assert.Equal(seed.DocumentId, hit.DocumentId);
        Assert.Equal(seed.ChunkIds[0], hit.DocumentChunkId);
        Assert.Equal(0, hit.ChunkIndex);
        Assert.Equal(0, hit.StartOffset);
        Assert.Equal(67, hit.EndOffset);
        Assert.Equal("Infrastructure monitoring provides observability into system health", hit.Text);
        Assert.True(hit.FtsRank > 0);
    }

    // ================================================================
    // 9. Nonmatching query returns empty
    // ================================================================

    [Fact]
    public async Task Retrieval_returns_empty_for_nonmatching_query()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithChunksAsync(orgId, projectId,
            ["Photosynthesis converts sunlight into chemical energy within chloroplasts"]);
        await WaitForFullTextPopulationAsync();

        var hits = await RetrieveAsync(orgId, projectId, "cryptocurrency blockchain");

        Assert.Empty(hits);
    }

    // ================================================================
    // 10. Multiple documents and chunks
    // ================================================================

    [Fact]
    public async Task Retrieval_returns_hits_across_multiple_documents()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithChunksAsync(orgId, projectId,
            ["Cloud computing enables elastic resource allocation for applications"]);
        await SeedDocumentWithChunksAsync(orgId, projectId,
            ["Cloud infrastructure scales dynamically to meet computing demands"]);
        await WaitForFullTextPopulationAsync();

        var hits = await RetrieveAsync(orgId, projectId, "cloud computing");

        Assert.Equal(2, hits.Count);
    }

    // ================================================================
    // 11. Special character query
    // ================================================================

    [Fact]
    public async Task Retrieval_handles_special_character_query_without_error()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithChunksAsync(orgId, projectId,
            ["The application uses advanced natural language processing techniques"]);
        await WaitForFullTextPopulationAsync();

        var hits = await RetrieveAsync(orgId, projectId, "C# .NET");

        // Should not throw — empty or non-empty is both acceptable
        Assert.NotNull(hits);
    }

    // ================================================================
    // 12. CRITICAL: global-top-N regression (no top_n_by_rank)
    // ================================================================

    [Fact]
    public async Task Retrieval_returns_target_project_hits_despite_many_stronger_other_tenant_matches()
    {
        var (targetOrgId, targetProjectId) = await SeedTenantAsync();
        await SeedDocumentWithChunksAsync(targetOrgId, targetProjectId,
            ["Cybersecurity protocols protect enterprise network infrastructure"]);

        var (otherOrgId, otherProjectId) = await SeedTenantAsync();
        for (int i = 0; i < 20; i++)
        {
            await SeedDocumentWithChunksAsync(otherOrgId, otherProjectId,
            [
                "Cybersecurity cybersecurity cybersecurity protocols protect enterprise network cybersecurity",
                "Cybersecurity advanced cybersecurity measures strengthen cybersecurity infrastructure defense",
                "Enterprise cybersecurity solutions deliver comprehensive cybersecurity threat protection",
            ]);
        }
        await WaitForFullTextPopulationAsync();

        var hits = await RetrieveAsync(targetOrgId, targetProjectId, "cybersecurity protocols", topK: 5);

        Assert.NotEmpty(hits);
        Assert.All(hits, h =>
        {
            Assert.Equal(targetOrgId, _fixture.CreateContext().Documents.Single(d => d.Id == h.DocumentId).OrganizationId);
        });
    }
}
