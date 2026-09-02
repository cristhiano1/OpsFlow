using Microsoft.Data.SqlTypes;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;
using OpsFlow.Domain.Organizations;
using OpsFlow.Domain.Projects;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Api.IntegrationTests.Documents;

[Collection(SqlServerCollection.Name)]
public sealed class SemanticChunkRetrievalIntegrationTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);
    private static readonly EmbeddingGeneratorIdentity DefaultIdentity = new(
        EmbeddingProfiles.SemanticV1Id,
        EmbeddingProfiles.SemanticV1ModelId,
        EmbeddingProfiles.SemanticV1Dimensions);

    private readonly SqlServerFixture _fixture;

    public SemanticChunkRetrievalIntegrationTests(SqlServerFixture fixture)
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

    private async Task<SeedResult> SeedDocumentWithEmbeddingsAsync(
        Guid orgId, Guid projectId,
        int chunkCount = 2,
        string profileId = EmbeddingProfiles.SemanticV1Id,
        string modelId = EmbeddingProfiles.SemanticV1ModelId,
        int dimensions = EmbeddingProfiles.SemanticV1Dimensions,
        Func<int, float[]>? vectorFactory = null,
        Guid? documentId = null)
    {
        await using var db = _fixture.CreateContext();

        var docId = documentId ?? Guid.NewGuid();
        db.Documents.Add(new Document(docId, orgId, projectId, "test.txt", "text/plain", 100, Timestamp));
        await db.SaveChangesAsync();

        var chunkTexts = Enumerable.Range(0, chunkCount).Select(i => $"chunk{i}").ToArray();
        var extractionText = chunkCount > 0 ? string.Concat(chunkTexts) : "empty";
        db.DocumentExtractions.Add(new DocumentExtraction(docId, extractionText, Timestamp));
        await db.SaveChangesAsync();

        db.DocumentChunkSets.Add(new DocumentChunkSet(docId, 1, chunkCount, Timestamp));
        var chunkIds = new List<Guid>();
        var offset = 0;
        for (int i = 0; i < chunkCount; i++)
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
            setId, docId, 1, profileId, modelId, dimensions, chunkCount, Timestamp);
        db.DocumentEmbeddingSets.Add(embeddingSet);

        for (int i = 0; i < chunkCount; i++)
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

    private async Task<IReadOnlyList<SemanticChunkHit>> RetrieveAsync(
        Guid orgId, Guid projectId,
        ReadOnlyMemory<float> queryVector,
        int topK = 10,
        EmbeddingGeneratorIdentity? identity = null)
    {
        await using var db = _fixture.CreateContext();
        var retriever = new EfSemanticChunkRetriever(db);
        return await retriever.RetrieveAsync(
            orgId, projectId, identity ?? DefaultIdentity,
            queryVector, topK, CancellationToken.None);
    }

    private sealed record SeedResult(Guid DocumentId, List<Guid> ChunkIds, Guid EmbeddingSetId);

    // ================================================================
    // A. Cosine ranking correctness
    // ================================================================

    [Fact]
    public async Task Retrieval_ranks_nearest_vector_first()
    {
        var (orgId, projectId) = await SeedTenantAsync();

        var identicalVec = MakeDirectionVector((0, 1.0f));
        var partialVec = MakeDirectionVector((0, 0.6f), (1, 0.8f));
        var orthogonalVec = MakeDirectionVector((1, 1.0f));

        await SeedDocumentWithEmbeddingsAsync(orgId, projectId, chunkCount: 3,
            vectorFactory: i => i switch
            {
                0 => identicalVec,
                1 => partialVec,
                2 => orthogonalVec,
                _ => throw new InvalidOperationException(),
            });

        var hits = await RetrieveAsync(orgId, projectId, MakeDirectionVector((0, 1.0f)));

        Assert.Equal(3, hits.Count);
        Assert.True(hits[0].CosineDistance <= hits[1].CosineDistance,
            $"Expected first hit closer: {hits[0].CosineDistance} <= {hits[1].CosineDistance}");
        Assert.True(hits[1].CosineDistance <= hits[2].CosineDistance,
            $"Expected second hit closer than third: {hits[1].CosineDistance} <= {hits[2].CosineDistance}");
    }

    // ================================================================
    // B. Tenant isolation
    // ================================================================

    [Fact]
    public async Task Retrieval_excludes_documents_from_other_organization()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithEmbeddingsAsync(orgId, projectId);

        var (otherOrgId, _) = await SeedTenantAsync();

        var hits = await RetrieveAsync(otherOrgId, projectId, MakeDirectionVector((0, 1.0f)));

        Assert.Empty(hits);
    }

    // ================================================================
    // C. Project isolation
    // ================================================================

    [Fact]
    public async Task Retrieval_excludes_documents_from_other_project()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithEmbeddingsAsync(orgId, projectId);

        await using var db = _fixture.CreateContext();
        var otherProjectId = Guid.NewGuid();
        db.Projects.Add(new Project(otherProjectId, orgId, "OtherProject", null, Timestamp));
        await db.SaveChangesAsync();

        var hits = await RetrieveAsync(orgId, otherProjectId, MakeDirectionVector((0, 1.0f)));

        Assert.Empty(hits);
    }

    // ================================================================
    // D. Profile isolation
    // ================================================================

    [Fact]
    public async Task Retrieval_excludes_embeddings_with_different_profile()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithEmbeddingsAsync(orgId, projectId, profileId: "other-profile");

        var hits = await RetrieveAsync(orgId, projectId, MakeDirectionVector((0, 1.0f)));

        Assert.Empty(hits);
    }

    // ================================================================
    // E. Model isolation
    // ================================================================

    [Fact]
    public async Task Retrieval_excludes_embeddings_with_different_model()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithEmbeddingsAsync(orgId, projectId, modelId: "text-embedding-3-large");

        var hits = await RetrieveAsync(orgId, projectId, MakeDirectionVector((0, 1.0f)));

        Assert.Empty(hits);
    }

    // ================================================================
    // F. Dimensions metadata filtering
    // ================================================================

    [Fact]
    public async Task Retrieval_excludes_embeddings_with_different_dimensions_metadata()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithEmbeddingsAsync(orgId, projectId, dimensions: 3072);

        var hits = await RetrieveAsync(orgId, projectId, MakeDirectionVector((0, 1.0f)));

        Assert.Empty(hits);
    }

    // ================================================================
    // G. Unembedded documents ignored
    // ================================================================

    [Fact]
    public async Task Retrieval_ignores_documents_without_embeddings()
    {
        var (orgId, projectId) = await SeedTenantAsync();

        await using var db = _fixture.CreateContext();
        var unembeddedDocId = Guid.NewGuid();
        db.Documents.Add(new Document(unembeddedDocId, orgId, projectId, "no-emb.txt", "text/plain", 50, Timestamp));
        await db.SaveChangesAsync();
        db.DocumentExtractions.Add(new DocumentExtraction(unembeddedDocId, "some text", Timestamp));
        await db.SaveChangesAsync();
        db.DocumentChunkSets.Add(new DocumentChunkSet(unembeddedDocId, 1, 1, Timestamp));
        db.DocumentChunks.Add(new DocumentChunk(Guid.NewGuid(), unembeddedDocId, 0, 0, 9, "some text"));
        await db.SaveChangesAsync();

        await SeedDocumentWithEmbeddingsAsync(orgId, projectId);

        var hits = await RetrieveAsync(orgId, projectId, MakeDirectionVector((0, 1.0f)));

        Assert.All(hits, h => Assert.NotEqual(unembeddedDocId, h.DocumentId));
    }

    // ================================================================
    // H. Zero-chunk documents produce no hits
    // ================================================================

    [Fact]
    public async Task Retrieval_returns_empty_for_zero_chunk_document()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithEmbeddingsAsync(orgId, projectId, chunkCount: 0);

        var hits = await RetrieveAsync(orgId, projectId, MakeDirectionVector((0, 1.0f)));

        Assert.Empty(hits);
    }

    // ================================================================
    // I. TopK enforced
    // ================================================================

    [Fact]
    public async Task Retrieval_limits_results_to_topk()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithEmbeddingsAsync(orgId, projectId, chunkCount: 5,
            vectorFactory: i => MakeDirectionVector((0, 1.0f - (i * 0.1f))));

        var hits = await RetrieveAsync(orgId, projectId, MakeDirectionVector((0, 1.0f)), topK: 3);

        Assert.Equal(3, hits.Count);
    }

    // ================================================================
    // J. Deterministic tie ordering
    // ================================================================

    [Fact]
    public async Task Retrieval_orders_by_document_id_then_chunk_index_on_distance_tie()
    {
        var (orgId, projectId) = await SeedTenantAsync();

        // SQL Server sorts uniqueidentifier by the last 6 bytes first, so
        // ...0010 < ...0020 in ASC order. Seed the higher-ID document first
        // to prove ordering comes from SQL Server, not insertion order.
        var sqlLowerId = new Guid("00000000-0000-0000-0000-000000000010");
        var sqlHigherId = new Guid("00000000-0000-0000-0000-000000000020");

        var sameVec = MakeDirectionVector((0, 0.5f), (1, 0.5f));
        await SeedDocumentWithEmbeddingsAsync(orgId, projectId, chunkCount: 2,
            vectorFactory: _ => sameVec, documentId: sqlHigherId);
        await SeedDocumentWithEmbeddingsAsync(orgId, projectId, chunkCount: 2,
            vectorFactory: _ => sameVec, documentId: sqlLowerId);

        var hits = await RetrieveAsync(orgId, projectId, MakeDirectionVector((0, 0.5f), (1, 0.5f)));

        Assert.Equal(4, hits.Count);

        var firstDistance = hits[0].CosineDistance;
        Assert.All(hits, h => Assert.Equal(firstDistance, h.CosineDistance, precision: 9));

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
    // K. Exact result metadata
    // ================================================================

    [Fact]
    public async Task Retrieval_returns_exact_chunk_metadata()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        var vec = MakeDirectionVector((0, 1.0f));
        var seed = await SeedDocumentWithEmbeddingsAsync(orgId, projectId, chunkCount: 1,
            vectorFactory: _ => vec);

        var hits = await RetrieveAsync(orgId, projectId, MakeDirectionVector((0, 1.0f)));

        Assert.Single(hits);
        var hit = hits[0];
        Assert.Equal(seed.DocumentId, hit.DocumentId);
        Assert.Equal(seed.ChunkIds[0], hit.DocumentChunkId);
        Assert.Equal(0, hit.ChunkIndex);
        Assert.Equal(0, hit.StartOffset);
        Assert.Equal(6, hit.EndOffset);
        Assert.Equal("chunk0", hit.Text);
        Assert.True(double.IsFinite(hit.CosineDistance));
        Assert.InRange(hit.CosineDistance, -1e-6, 1e-6);
    }

    // ================================================================
    // L. Server-side query execution
    // ================================================================

    [Fact]
    public async Task Retrieval_executes_full_query_against_sql_server()
    {
        var (orgId, projectId) = await SeedTenantAsync();
        await SeedDocumentWithEmbeddingsAsync(orgId, projectId, chunkCount: 2,
            vectorFactory: i => MakeDirectionVector((0, i == 0 ? 1.0f : 0.0f), (1, i == 0 ? 0.0f : 1.0f)));

        var hits = await RetrieveAsync(orgId, projectId, MakeDirectionVector((0, 1.0f)));

        Assert.Equal(2, hits.Count);
        Assert.True(hits[0].CosineDistance < hits[1].CosineDistance);
    }

    // ================================================================
    // M. Corruption defense — EmbeddingSet.DocumentId != Chunk.DocumentId
    // ================================================================

    [Fact]
    public async Task Retrieval_excludes_row_where_embedding_set_document_differs_from_chunk_document()
    {
        var (orgId, projectId) = await SeedTenantAsync();

        await using var db = _fixture.CreateContext();

        var docAId = Guid.NewGuid();
        var docBId = Guid.NewGuid();

        db.Documents.Add(new Document(docAId, orgId, projectId, "a.txt", "text/plain", 10, Timestamp));
        db.Documents.Add(new Document(docBId, orgId, projectId, "b.txt", "text/plain", 10, Timestamp));
        await db.SaveChangesAsync();

        db.DocumentExtractions.Add(new DocumentExtraction(docAId, "textA", Timestamp));
        db.DocumentExtractions.Add(new DocumentExtraction(docBId, "textB", Timestamp));
        await db.SaveChangesAsync();

        db.DocumentChunkSets.Add(new DocumentChunkSet(docAId, 1, 1, Timestamp));
        db.DocumentChunkSets.Add(new DocumentChunkSet(docBId, 1, 1, Timestamp));

        var chunkAId = Guid.NewGuid();
        var chunkBId = Guid.NewGuid();
        db.DocumentChunks.Add(new DocumentChunk(chunkAId, docAId, 0, 0, 5, "textA"));
        db.DocumentChunks.Add(new DocumentChunk(chunkBId, docBId, 0, 0, 5, "textB"));
        await db.SaveChangesAsync();

        var setForDocA = Guid.NewGuid();
        db.DocumentEmbeddingSets.Add(new DocumentEmbeddingSet(
            setForDocA, docAId, 1, EmbeddingProfiles.SemanticV1Id,
            EmbeddingProfiles.SemanticV1ModelId, EmbeddingProfiles.SemanticV1Dimensions, 1, Timestamp));
        await db.SaveChangesAsync();

        db.Set<DocumentChunkEmbeddingRow>().Add(new DocumentChunkEmbeddingRow
        {
            EmbeddingSetId = setForDocA,
            DocumentChunkId = chunkAId,
            Embedding = new SqlVector<float>(MakeDirectionVector((0, 1.0f))),
        });
        await db.SaveChangesAsync();

        db.Set<DocumentChunkEmbeddingRow>().Add(new DocumentChunkEmbeddingRow
        {
            EmbeddingSetId = setForDocA,
            DocumentChunkId = chunkBId,
            Embedding = new SqlVector<float>(MakeDirectionVector((0, 1.0f))),
        });
        await db.SaveChangesAsync();

        var hits = await RetrieveAsync(orgId, projectId, MakeDirectionVector((0, 1.0f)));

        Assert.Single(hits);
        Assert.Equal(docAId, hits[0].DocumentId);
        Assert.Equal(chunkAId, hits[0].DocumentChunkId);
    }

    // ================================================================
    // Multiple documents, mixed profiles
    // ================================================================

    [Fact]
    public async Task Retrieval_returns_only_matching_profile_across_documents()
    {
        var (orgId, projectId) = await SeedTenantAsync();

        await SeedDocumentWithEmbeddingsAsync(orgId, projectId, chunkCount: 1,
            profileId: EmbeddingProfiles.SemanticV1Id,
            modelId: EmbeddingProfiles.SemanticV1ModelId,
            vectorFactory: _ => MakeDirectionVector((0, 1.0f)));

        await SeedDocumentWithEmbeddingsAsync(orgId, projectId, chunkCount: 1,
            profileId: "other-profile-v2",
            modelId: "other-model",
            vectorFactory: _ => MakeDirectionVector((0, 1.0f)));

        var hits = await RetrieveAsync(orgId, projectId, MakeDirectionVector((0, 1.0f)));

        Assert.Single(hits);
    }
}
