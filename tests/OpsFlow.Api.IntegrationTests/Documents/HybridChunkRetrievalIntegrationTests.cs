using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;
using OpsFlow.Domain.Organizations;
using OpsFlow.Domain.Projects;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Api.IntegrationTests.Documents;

[Collection(SqlServerCollection.Name)]
public sealed class HybridChunkRetrievalIntegrationTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly EmbeddingGeneratorIdentity DefaultIdentity = new(
        EmbeddingProfiles.SemanticV1Id,
        EmbeddingProfiles.SemanticV1ModelId,
        EmbeddingProfiles.SemanticV1Dimensions);

    private readonly SqlServerFixture _fixture;

    public HybridChunkRetrievalIntegrationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static float[] MakeDirectionVector(params (int Index, float Value)[] components)
    {
        var v = new float[EmbeddingProfiles.SemanticV1Dimensions];
        foreach (var (index, value) in components)
        {
            v[index] = value;
        }

        return v;
    }

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

    private async Task<SeedResult> SeedDocumentWithChunksAndEmbeddingsAsync(
        Guid orgId,
        Guid projectId,
        string[] chunkTexts,
        Func<int, float[]>? vectorFactory = null,
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

        var setId = Guid.NewGuid();
        var embeddingSet = new DocumentEmbeddingSet(
            setId, docId, 1, EmbeddingProfiles.SemanticV1Id,
            EmbeddingProfiles.SemanticV1ModelId, EmbeddingProfiles.SemanticV1Dimensions,
            chunkTexts.Length, Timestamp);
        db.DocumentEmbeddingSets.Add(embeddingSet);

        for (int i = 0; i < chunkTexts.Length; i++)
        {
            var vec = vectorFactory?.Invoke(i) ?? MakeDirectionVector((0, (i + 1) * 0.1f));
            db.Set<DocumentChunkEmbeddingRow>().Add(new DocumentChunkEmbeddingRow
            {
                EmbeddingSetId = setId,
                DocumentChunkId = chunkIds[i],
                Embedding = new SqlVector<float>(vec),
            });
        }

        await db.SaveChangesAsync();

        return new SeedResult(docId, chunkIds, setId);
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

    private sealed record SeedResult(Guid DocumentId, List<Guid> ChunkIds, Guid EmbeddingSetId);

    // ================================================================
    // A. Hybrid retrieval with overlap — both sources contribute
    // ================================================================

    [Fact]
    public async Task Hybrid_retrieval_fuses_overlapping_semantic_and_lexical_hits()
    {
        var (orgId, projectId) = await SeedTenantAsync();

        var seed = await SeedDocumentWithChunksAndEmbeddingsAsync(
            orgId, projectId,
            ["Machine learning algorithms optimize neural network training processes"],
            vectorFactory: _ => MakeDirectionVector((0, 1.0f)));
        await WaitForFullTextPopulationAsync();

        await using var db = _fixture.CreateContext();
        var semanticRetriever = new EfSemanticChunkRetriever(db);
        var lexicalRetriever = new EfLexicalChunkRetriever(db);

        var queryVector = MakeDirectionVector((0, 1.0f));
        var semanticHits = await semanticRetriever.RetrieveAsync(
            orgId, projectId, DefaultIdentity, queryVector, 50, CancellationToken.None);
        var lexicalHits = await lexicalRetriever.RetrieveAsync(
            orgId, projectId, "machine learning algorithms", 50, CancellationToken.None);

        Assert.NotEmpty(semanticHits);
        Assert.NotEmpty(lexicalHits);

        var fusedHits = ReciprocalRankFusion.Fuse(semanticHits, lexicalHits, 10);

        var overlapping = fusedHits.SingleOrDefault(h => h.DocumentChunkId == seed.ChunkIds[0]);
        Assert.NotNull(overlapping);
        Assert.NotNull(overlapping.SemanticRank);
        Assert.NotNull(overlapping.LexicalRank);

        var expectedScore =
            (1.0 / (60 + overlapping.SemanticRank.Value)) +
            (1.0 / (60 + overlapping.LexicalRank.Value));
        Assert.Equal(expectedScore, overlapping.RrfScore);
    }

    // ================================================================
    // B. Semantic contributes, lexical returns empty
    // ================================================================

    [Fact]
    public async Task Hybrid_retrieval_succeeds_with_zero_lexical_hits()
    {
        var (orgId, projectId) = await SeedTenantAsync();

        await SeedDocumentWithChunksAndEmbeddingsAsync(
            orgId, projectId,
            ["Photosynthesis converts sunlight into chemical energy within chloroplasts"],
            vectorFactory: _ => MakeDirectionVector((0, 1.0f)));
        await WaitForFullTextPopulationAsync();

        await using var db = _fixture.CreateContext();
        var semanticRetriever = new EfSemanticChunkRetriever(db);
        var lexicalRetriever = new EfLexicalChunkRetriever(db);

        var queryVector = MakeDirectionVector((0, 1.0f));
        var semanticHits = await semanticRetriever.RetrieveAsync(
            orgId, projectId, DefaultIdentity, queryVector, 50, CancellationToken.None);
        var lexicalHits = await lexicalRetriever.RetrieveAsync(
            orgId, projectId, "cryptocurrency blockchain", 50, CancellationToken.None);

        Assert.NotEmpty(semanticHits);
        Assert.Empty(lexicalHits);

        var fusedHits = ReciprocalRankFusion.Fuse(semanticHits, lexicalHits, 10);

        Assert.NotEmpty(fusedHits);
        Assert.All(fusedHits, h =>
        {
            Assert.NotNull(h.SemanticRank);
            Assert.Null(h.LexicalRank);
        });
    }

    // ================================================================
    // C. Lexical contributes, semantic returns empty
    // ================================================================

    [Fact]
    public async Task Hybrid_retrieval_succeeds_with_zero_semantic_hits()
    {
        var (orgId, projectId) = await SeedTenantAsync();

        await SeedDocumentWithChunksAndEmbeddingsAsync(
            orgId, projectId,
            ["Cloud computing enables elastic resource allocation for applications"],
            vectorFactory: _ => MakeDirectionVector((0, 1.0f)));
        await WaitForFullTextPopulationAsync();

        await using var db = _fixture.CreateContext();
        var semanticRetriever = new EfSemanticChunkRetriever(db);
        var lexicalRetriever = new EfLexicalChunkRetriever(db);

        var orthogonalQuery = MakeDirectionVector((1, 1.0f));
        var semanticHits = await semanticRetriever.RetrieveAsync(
            orgId, projectId,
            new EmbeddingGeneratorIdentity("other-profile", "other-model", EmbeddingProfiles.SemanticV1Dimensions),
            orthogonalQuery, 50, CancellationToken.None);
        var lexicalHits = await lexicalRetriever.RetrieveAsync(
            orgId, projectId, "cloud computing", 50, CancellationToken.None);

        Assert.Empty(semanticHits);
        Assert.NotEmpty(lexicalHits);

        var fusedHits = ReciprocalRankFusion.Fuse(semanticHits, lexicalHits, 10);

        Assert.NotEmpty(fusedHits);
        Assert.All(fusedHits, h =>
        {
            Assert.Null(h.SemanticRank);
            Assert.NotNull(h.LexicalRank);
        });
    }
}
