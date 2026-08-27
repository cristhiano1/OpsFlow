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
public sealed class DocumentEmbeddingIntegrationTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
    private readonly SqlServerFixture _fixture;

    public DocumentEmbeddingIntegrationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Guid OrgId, Guid ProjectId, Guid DocumentId)> SeedDocumentWithChunksAsync(
        int chunkCount = 2)
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

        var documentId = Guid.NewGuid();
        db.Documents.Add(new Document(documentId, orgId, projectId, "test.txt", "text/plain", 100, Timestamp));
        await db.SaveChangesAsync();

        var chunkTexts = Enumerable.Range(0, chunkCount).Select(i => $"chunk{i}").ToArray();
        var extractionText = string.Concat(chunkTexts);
        db.DocumentExtractions.Add(new DocumentExtraction(documentId, extractionText.Length > 0 ? extractionText : "empty", Timestamp));
        await db.SaveChangesAsync();

        db.DocumentChunkSets.Add(new DocumentChunkSet(documentId, 1, chunkCount, Timestamp));
        var offset = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            var text = chunkTexts[i];
            db.DocumentChunks.Add(new DocumentChunk(Guid.NewGuid(), documentId, i, offset, offset + text.Length, text));
            offset += text.Length;
        }
        await db.SaveChangesAsync();

        return (orgId, projectId, documentId);
    }

    private static float[] MakeVector(int dimensions = 1536)
    {
        var v = new float[dimensions];
        for (int i = 0; i < dimensions; i++)
        {
            v[i] = (i + 1) * 0.001f;
        }
        return v;
    }

    // ================================================================
    // EmbeddingSet + rows round-trip
    // ================================================================

    [Fact]
    public async Task EmbeddingSet_and_rows_persist_and_read_back()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(2);

        Guid setId;
        List<Guid> chunkIds;

        await using (var db = _fixture.CreateContext())
        {
            chunkIds = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .OrderBy(c => c.ChunkIndex)
                .Select(c => c.Id)
                .ToListAsync();

            setId = Guid.NewGuid();
            var embeddingSet = new DocumentEmbeddingSet(
                setId, documentId, 1, "opsflow-semantic-v1",
                "text-embedding-3-small", 1536, 2, Timestamp);

            db.DocumentEmbeddingSets.Add(embeddingSet);
            db.Set<DocumentChunkEmbeddingRow>().AddRange(
                chunkIds.Select(cid => new DocumentChunkEmbeddingRow
                {
                    EmbeddingSetId = setId,
                    DocumentChunkId = cid,
                    Embedding = new SqlVector<float>(MakeVector()),
                }));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var loaded = await db.DocumentEmbeddingSets
                .AsNoTracking()
                .FirstOrDefaultAsync(es => es.Id == setId);

            Assert.NotNull(loaded);
            Assert.Equal(documentId, loaded.DocumentId);
            Assert.Equal("opsflow-semantic-v1", loaded.ProfileId);
            Assert.Equal("text-embedding-3-small", loaded.ModelId);
            Assert.Equal(1536, loaded.Dimensions);
            Assert.Equal(2, loaded.EmbeddingCount);

            var rows = await db.Set<DocumentChunkEmbeddingRow>()
                .AsNoTracking()
                .Where(r => r.EmbeddingSetId == setId)
                .ToListAsync();

            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(1536, r.Embedding.Length));
        }
    }

    // ================================================================
    // Unique(DocumentId, ProfileId) enforced
    // ================================================================

    [Fact]
    public async Task Duplicate_document_profile_pair_is_rejected()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        await using (var db = _fixture.CreateContext())
        {
            db.DocumentEmbeddingSets.Add(new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model-a", 1536, 1, Timestamp));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            db.DocumentEmbeddingSets.Add(new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model-b", 1536, 1, Timestamp));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    // ================================================================
    // FK cascade — deleting EmbeddingSet cascades to rows
    // ================================================================

    [Fact]
    public async Task Deleting_embedding_set_cascades_to_rows()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        var setId = Guid.NewGuid();
        Guid chunkId;

        await using (var db = _fixture.CreateContext())
        {
            chunkId = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .Select(c => c.Id)
                .FirstAsync();

            db.DocumentEmbeddingSets.Add(new DocumentEmbeddingSet(
                setId, documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 1, Timestamp));
            db.Set<DocumentChunkEmbeddingRow>().Add(new DocumentChunkEmbeddingRow
            {
                EmbeddingSetId = setId,
                DocumentChunkId = chunkId,
                Embedding = new SqlVector<float>(MakeVector()),
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var set = await db.DocumentEmbeddingSets.FindAsync(setId);
            Assert.NotNull(set);
            db.DocumentEmbeddingSets.Remove(set);
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Empty(await db.Set<DocumentChunkEmbeddingRow>()
                .Where(r => r.EmbeddingSetId == setId)
                .ToListAsync());
        }
    }

    // ================================================================
    // FK cascade — deleting ChunkSet cascades to EmbeddingSet
    // ================================================================

    [Fact]
    public async Task Deleting_chunk_set_cascades_to_embedding_set()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        var setId = Guid.NewGuid();

        await using (var db = _fixture.CreateContext())
        {
            var chunkId = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .Select(c => c.Id)
                .FirstAsync();

            db.DocumentEmbeddingSets.Add(new DocumentEmbeddingSet(
                setId, documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 1, Timestamp));
            db.Set<DocumentChunkEmbeddingRow>().Add(new DocumentChunkEmbeddingRow
            {
                EmbeddingSetId = setId,
                DocumentChunkId = chunkId,
                Embedding = new SqlVector<float>(MakeVector()),
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var chunkSet = await db.DocumentChunkSets.FindAsync(documentId);
            Assert.NotNull(chunkSet);
            db.DocumentChunkSets.Remove(chunkSet);
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Null(await db.DocumentEmbeddingSets.FindAsync(setId));
        }
    }

    // ================================================================
    // FK RESTRICT — cannot delete chunk that has embedding row
    // ================================================================

    [Fact]
    public async Task Cannot_delete_chunk_referenced_by_embedding_row()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        Guid chunkId;

        await using (var db = _fixture.CreateContext())
        {
            chunkId = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .Select(c => c.Id)
                .FirstAsync();

            var setId = Guid.NewGuid();
            db.DocumentEmbeddingSets.Add(new DocumentEmbeddingSet(
                setId, documentId, 1, "profile", "model", 1536, 1, Timestamp));
            db.Set<DocumentChunkEmbeddingRow>().Add(new DocumentChunkEmbeddingRow
            {
                EmbeddingSetId = setId,
                DocumentChunkId = chunkId,
                Embedding = new SqlVector<float>(MakeVector()),
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var chunk = await db.DocumentChunks.FindAsync(chunkId);
            Assert.NotNull(chunk);
            db.DocumentChunks.Remove(chunk);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    // ================================================================
    // Repository — AddIfAbsent persists atomically
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_persists_set_and_rows()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(2);

        List<Guid> chunkIds;

        await using (var db = _fixture.CreateContext())
        {
            chunkIds = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .OrderBy(c => c.ChunkIndex)
                .Select(c => c.Id)
                .ToListAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "text-embedding-3-small", 1536, 2, Timestamp);

            var embeddings = chunkIds.Select(cid =>
                new ChunkEmbeddingInput(cid, MakeVector())).ToList();

            var result = await repo.AddIfAbsentAsync(
                embeddingSet, embeddings, projectId, orgId, CancellationToken.None);

            Assert.Equal(DocumentEmbeddingSetAddStatus.Added, result.Status);
        }

        await using (var db = _fixture.CreateContext())
        {
            var loaded = await db.DocumentEmbeddingSets
                .AsNoTracking()
                .FirstOrDefaultAsync(es => es.DocumentId == documentId);
            Assert.NotNull(loaded);

            var rows = await db.Set<DocumentChunkEmbeddingRow>()
                .AsNoTracking()
                .Where(r => r.EmbeddingSetId == loaded.Id)
                .ToListAsync();
            Assert.Equal(2, rows.Count);
        }
    }

    // ================================================================
    // Repository — AddIfAbsent returns AlreadyExists on duplicate
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_returns_AlreadyExists_on_duplicate_profile()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        Guid chunkId;

        await using (var db = _fixture.CreateContext())
        {
            chunkId = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .Select(c => c.Id)
                .FirstAsync();

            db.DocumentEmbeddingSets.Add(new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "text-embedding-3-small", 1536, 1, Timestamp));
            db.Set<DocumentChunkEmbeddingRow>().Add(new DocumentChunkEmbeddingRow
            {
                EmbeddingSetId = db.DocumentEmbeddingSets.Local.First().Id,
                DocumentChunkId = chunkId,
                Embedding = new SqlVector<float>(MakeVector()),
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model-b", 1536, 1, Timestamp);
            var embeddings = new List<ChunkEmbeddingInput>
            {
                new(chunkId, MakeVector()),
            };

            var result = await repo.AddIfAbsentAsync(
                embeddingSet, embeddings, projectId, orgId, CancellationToken.None);

            Assert.Equal(DocumentEmbeddingSetAddStatus.AlreadyExists, result.Status);
            Assert.NotNull(result.EmbeddingSet);
            Assert.Equal(documentId, result.EmbeddingSet.DocumentId);
        }
    }

    // ================================================================
    // Repository — AddIfAbsent returns NotFound for wrong tenant
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_returns_NotFound_for_wrong_org()
    {
        var (_, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        await using var db = _fixture.CreateContext();
        var repo = new EfDocumentEmbeddingSetRepository(db);
        var embeddingSet = new DocumentEmbeddingSet(
            Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
            "model", 1536, 0, Timestamp);

        var result = await repo.AddIfAbsentAsync(
            embeddingSet, [], projectId, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(DocumentEmbeddingSetAddStatus.NotFound, result.Status);
    }

    // ================================================================
    // Repository — GetByDocumentAndProfile tenant isolation
    // ================================================================

    [Fact]
    public async Task Repository_GetByDocumentAndProfile_returns_null_for_wrong_org()
    {
        var (_, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        await using (var db = _fixture.CreateContext())
        {
            db.DocumentEmbeddingSets.Add(new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 1, Timestamp));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var result = await repo.GetByDocumentAndProfileAsync(
                documentId, "opsflow-semantic-v1", projectId, Guid.NewGuid(), CancellationToken.None);

            Assert.Null(result);
        }
    }

    [Fact]
    public async Task Repository_GetByDocumentAndProfile_returns_set_for_correct_tenant()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        await using (var db = _fixture.CreateContext())
        {
            db.DocumentEmbeddingSets.Add(new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 1, Timestamp));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var result = await repo.GetByDocumentAndProfileAsync(
                documentId, "opsflow-semantic-v1", projectId, orgId, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(documentId, result.DocumentId);
        }
    }

    // ================================================================
    // SnapshotReader — round-trip
    // ================================================================

    [Fact]
    public async Task SnapshotReader_returns_snapshot_with_ordered_chunks()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(3);

        await using var db = _fixture.CreateContext();
        var reader = new EfDocumentChunkSnapshotReader(db);
        var snapshot = await reader.GetByDocumentAsync(documentId, projectId, orgId, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(documentId, snapshot.DocumentId);
        Assert.Equal(1, snapshot.ChunkingVersion);
        Assert.Equal(3, snapshot.ChunkCount);
        Assert.Equal(3, snapshot.Chunks.Count);

        for (int i = 0; i < snapshot.Chunks.Count; i++)
        {
            Assert.Equal(i, snapshot.Chunks[i].ChunkIndex);
            Assert.NotEqual(Guid.Empty, snapshot.Chunks[i].ChunkId);
        }
    }

    // ================================================================
    // SnapshotReader — tenant isolation
    // ================================================================

    [Fact]
    public async Task SnapshotReader_returns_null_for_wrong_org()
    {
        var (_, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        await using var db = _fixture.CreateContext();
        var reader = new EfDocumentChunkSnapshotReader(db);
        var result = await reader.GetByDocumentAsync(documentId, projectId, Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    // ================================================================
    // SnapshotReader — returns null when no chunks
    // ================================================================

    [Fact]
    public async Task SnapshotReader_returns_null_when_no_chunk_set()
    {
        await using var db = _fixture.CreateContext();

        var orgId = Guid.NewGuid();
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = "SnapshotOrg-" + orgId.ToString("N")[..8],
            Slug = "snapshot-org-" + orgId.ToString("N")[..8],
            CreatedAt = Timestamp,
        });
        await db.SaveChangesAsync();

        var projectId = Guid.NewGuid();
        db.Projects.Add(new Project(projectId, orgId, "P", null, Timestamp));
        await db.SaveChangesAsync();

        var documentId = Guid.NewGuid();
        db.Documents.Add(new Document(documentId, orgId, projectId, "f.txt", "text/plain", 10, Timestamp));
        await db.SaveChangesAsync();

        var reader = new EfDocumentChunkSnapshotReader(db);
        var result = await reader.GetByDocumentAsync(documentId, projectId, orgId, CancellationToken.None);

        Assert.Null(result);
    }

    // ================================================================
    // Vector round-trip — values preserved
    // ================================================================

    [Fact]
    public async Task Vector_values_survive_round_trip()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);
        var vector = MakeVector();
        Guid setId;
        Guid chunkId;

        await using (var db = _fixture.CreateContext())
        {
            chunkId = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .Select(c => c.Id)
                .FirstAsync();

            setId = Guid.NewGuid();
            db.DocumentEmbeddingSets.Add(new DocumentEmbeddingSet(
                setId, documentId, 1, "profile", "model", 1536, 1, Timestamp));
            db.Set<DocumentChunkEmbeddingRow>().Add(new DocumentChunkEmbeddingRow
            {
                EmbeddingSetId = setId,
                DocumentChunkId = chunkId,
                Embedding = new SqlVector<float>(vector),
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var row = await db.Set<DocumentChunkEmbeddingRow>()
                .AsNoTracking()
                .FirstAsync(r => r.EmbeddingSetId == setId && r.DocumentChunkId == chunkId);

            Assert.Equal(1536, row.Embedding.Length);
            var loadedValues = row.Embedding.Memory.ToArray();
            for (int i = 0; i < vector.Length; i++)
            {
                Assert.Equal(vector[i], loadedValues[i], precision: 5);
            }
        }
    }

    // ================================================================
    // Zero-chunk embedding set
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_persists_zero_chunk_embedding_set()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(0);

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 0, Timestamp);

            var result = await repo.AddIfAbsentAsync(
                embeddingSet, [], projectId, orgId, CancellationToken.None);

            Assert.Equal(DocumentEmbeddingSetAddStatus.Added, result.Status);
        }

        await using (var db = _fixture.CreateContext())
        {
            var loaded = await db.DocumentEmbeddingSets
                .AsNoTracking()
                .FirstOrDefaultAsync(es => es.DocumentId == documentId);
            Assert.NotNull(loaded);
            Assert.Equal(0, loaded.EmbeddingCount);
        }
    }

    // ================================================================
    // Repository input guards — EmbeddingCount mismatch
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_rejects_embedding_count_mismatch()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(2);

        List<Guid> chunkIds;
        await using (var db = _fixture.CreateContext())
        {
            chunkIds = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .OrderBy(c => c.ChunkIndex)
                .Select(c => c.Id)
                .ToListAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 2, Timestamp);

            var embeddings = new List<ChunkEmbeddingInput>
            {
                new(chunkIds[0], MakeVector()),
            };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.AddIfAbsentAsync(embeddingSet, embeddings, projectId, orgId, CancellationToken.None));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Empty(await db.DocumentEmbeddingSets
                .AsNoTracking()
                .Where(es => es.DocumentId == documentId)
                .ToListAsync());
        }
    }

    // ================================================================
    // Repository input guards — cross-document chunk ID
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_rejects_cross_document_chunk_ids()
    {
        var (orgA, projectA, documentA) = await SeedDocumentWithChunksAsync(1);
        var (_, _, documentB) = await SeedDocumentWithChunksAsync(1);

        Guid chunkIdFromB;
        await using (var db = _fixture.CreateContext())
        {
            chunkIdFromB = await db.DocumentChunks
                .Where(c => c.DocumentId == documentB)
                .Select(c => c.Id)
                .FirstAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentA, 1, "opsflow-semantic-v1",
                "model", 1536, 1, Timestamp);

            var embeddings = new List<ChunkEmbeddingInput>
            {
                new(chunkIdFromB, MakeVector()),
            };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.AddIfAbsentAsync(embeddingSet, embeddings, projectA, orgA, CancellationToken.None));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Empty(await db.DocumentEmbeddingSets
                .AsNoTracking()
                .Where(es => es.DocumentId == documentA)
                .ToListAsync());
        }
    }

    // ================================================================
    // Repository input guards — empty DocumentChunkId
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_rejects_empty_chunk_id()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 1, Timestamp);

            var embeddings = new List<ChunkEmbeddingInput>
            {
                new(Guid.Empty, MakeVector()),
            };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.AddIfAbsentAsync(embeddingSet, embeddings, projectId, orgId, CancellationToken.None));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Empty(await db.DocumentEmbeddingSets
                .AsNoTracking()
                .Where(es => es.DocumentId == documentId)
                .ToListAsync());
        }
    }

    // ================================================================
    // Repository input guards — duplicate DocumentChunkIds
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_rejects_duplicate_chunk_ids()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        Guid chunkId;
        await using (var db = _fixture.CreateContext())
        {
            chunkId = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .Select(c => c.Id)
                .FirstAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 2, Timestamp);

            var embeddings = new List<ChunkEmbeddingInput>
            {
                new(chunkId, MakeVector()),
                new(chunkId, MakeVector()),
            };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.AddIfAbsentAsync(embeddingSet, embeddings, projectId, orgId, CancellationToken.None));
        }
    }

    // ================================================================
    // Repository input guards — wrong vector dimension
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_rejects_wrong_vector_dimension()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        Guid chunkId;
        await using (var db = _fixture.CreateContext())
        {
            chunkId = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .Select(c => c.Id)
                .FirstAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 1, Timestamp);

            var embeddings = new List<ChunkEmbeddingInput>
            {
                new(chunkId, new float[3]),
            };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.AddIfAbsentAsync(embeddingSet, embeddings, projectId, orgId, CancellationToken.None));
        }
    }

    // ================================================================
    // Repository input guards — NaN in vector
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_rejects_NaN_in_vector()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        Guid chunkId;
        await using (var db = _fixture.CreateContext())
        {
            chunkId = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .Select(c => c.Id)
                .FirstAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 1, Timestamp);

            var vector = MakeVector();
            vector[0] = float.NaN;

            var embeddings = new List<ChunkEmbeddingInput>
            {
                new(chunkId, vector),
            };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.AddIfAbsentAsync(embeddingSet, embeddings, projectId, orgId, CancellationToken.None));
        }
    }

    // ================================================================
    // Repository input guards — PositiveInfinity in vector
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_rejects_positive_infinity_in_vector()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        Guid chunkId;
        await using (var db = _fixture.CreateContext())
        {
            chunkId = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .Select(c => c.Id)
                .FirstAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 1, Timestamp);

            var vector = MakeVector();
            vector[0] = float.PositiveInfinity;

            var embeddings = new List<ChunkEmbeddingInput>
            {
                new(chunkId, vector),
            };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.AddIfAbsentAsync(embeddingSet, embeddings, projectId, orgId, CancellationToken.None));
        }
    }

    // ================================================================
    // Repository input guards — NegativeInfinity in vector
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_rejects_negative_infinity_in_vector()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        Guid chunkId;
        await using (var db = _fixture.CreateContext())
        {
            chunkId = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .Select(c => c.Id)
                .FirstAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 1, Timestamp);

            var vector = MakeVector();
            vector[0] = float.NegativeInfinity;

            var embeddings = new List<ChunkEmbeddingInput>
            {
                new(chunkId, vector),
            };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.AddIfAbsentAsync(embeddingSet, embeddings, projectId, orgId, CancellationToken.None));
        }
    }

    // ================================================================
    // Repository input guards — source chunk count mismatch
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_rejects_source_chunk_count_mismatch()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(2);

        List<Guid> chunkIds;
        await using (var db = _fixture.CreateContext())
        {
            chunkIds = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .OrderBy(c => c.ChunkIndex)
                .Select(c => c.Id)
                .ToListAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 1, Timestamp);

            var embeddings = new List<ChunkEmbeddingInput>
            {
                new(chunkIds[0], MakeVector()),
            };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.AddIfAbsentAsync(embeddingSet, embeddings, projectId, orgId, CancellationToken.None));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Empty(await db.DocumentEmbeddingSets
                .AsNoTracking()
                .Where(es => es.DocumentId == documentId)
                .ToListAsync());
        }
    }

    // ================================================================
    // Repository input guards — zero embeddings with non-zero source chunks
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_rejects_zero_embeddings_when_source_has_chunks()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(2);

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 0, Timestamp);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.AddIfAbsentAsync(embeddingSet, [], projectId, orgId, CancellationToken.None));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Empty(await db.DocumentEmbeddingSets
                .AsNoTracking()
                .Where(es => es.DocumentId == documentId)
                .ToListAsync());
        }
    }

    // ================================================================
    // Repository input guards — chunking version mismatch
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_rejects_chunking_version_mismatch()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        Guid chunkId;
        await using (var db = _fixture.CreateContext())
        {
            chunkId = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .Select(c => c.Id)
                .FirstAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 2, "opsflow-semantic-v1",
                "model", 1536, 1, Timestamp);

            var embeddings = new List<ChunkEmbeddingInput>
            {
                new(chunkId, MakeVector()),
            };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.AddIfAbsentAsync(embeddingSet, embeddings, projectId, orgId, CancellationToken.None));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Empty(await db.DocumentEmbeddingSets
                .AsNoTracking()
                .Where(es => es.DocumentId == documentId)
                .ToListAsync());
        }
    }

    // ================================================================
    // Repository input guards — embedding metadata dimension mismatch
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_rejects_metadata_dimension_mismatch()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithChunksAsync(1);

        Guid chunkId;
        await using (var db = _fixture.CreateContext())
        {
            chunkId = await db.DocumentChunks
                .Where(c => c.DocumentId == documentId)
                .Select(c => c.Id)
                .FirstAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 384, 1, Timestamp);

            var embeddings = new List<ChunkEmbeddingInput>
            {
                new(chunkId, MakeVector()),
            };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.AddIfAbsentAsync(embeddingSet, embeddings, projectId, orgId, CancellationToken.None));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Empty(await db.DocumentEmbeddingSets
                .AsNoTracking()
                .Where(es => es.DocumentId == documentId)
                .ToListAsync());
        }
    }

    // ================================================================
    // Repository input guards — inconsistent source artifact (extra rows)
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_rejects_inconsistent_source_artifact()
    {
        await using var seedDb = _fixture.CreateContext();

        var orgId = Guid.NewGuid();
        seedDb.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = "InconsistentOrg-" + orgId.ToString("N")[..8],
            Slug = "inconsistent-org-" + orgId.ToString("N")[..8],
            CreatedAt = Timestamp,
        });
        await seedDb.SaveChangesAsync();

        var projectId = Guid.NewGuid();
        seedDb.Projects.Add(new Project(projectId, orgId, "P", null, Timestamp));
        await seedDb.SaveChangesAsync();

        var documentId = Guid.NewGuid();
        seedDb.Documents.Add(new Document(documentId, orgId, projectId, "f.txt", "text/plain", 10, Timestamp));
        await seedDb.SaveChangesAsync();

        seedDb.DocumentExtractions.Add(new DocumentExtraction(documentId, "aabb", Timestamp));
        await seedDb.SaveChangesAsync();

        seedDb.DocumentChunkSets.Add(new DocumentChunkSet(documentId, 1, 1, Timestamp));
        var chunkId1 = Guid.NewGuid();
        var chunkId2 = Guid.NewGuid();
        seedDb.DocumentChunks.Add(new DocumentChunk(chunkId1, documentId, 0, 0, 2, "aa"));
        seedDb.DocumentChunks.Add(new DocumentChunk(chunkId2, documentId, 1, 2, 4, "bb"));
        await seedDb.SaveChangesAsync();

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 1, Timestamp);

            var embeddings = new List<ChunkEmbeddingInput>
            {
                new(chunkId1, MakeVector()),
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repo.AddIfAbsentAsync(embeddingSet, embeddings, projectId, orgId, CancellationToken.None));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Empty(await db.DocumentEmbeddingSets
                .AsNoTracking()
                .Where(es => es.DocumentId == documentId)
                .ToListAsync());
            Assert.Empty(await db.Set<DocumentChunkEmbeddingRow>()
                .AsNoTracking()
                .Where(r => r.EmbeddingSetId != Guid.Empty)
                .Join(db.DocumentEmbeddingSets,
                    r => r.EmbeddingSetId, es => es.Id,
                    (r, es) => new { r, es })
                .Where(x => x.es.DocumentId == documentId)
                .ToListAsync());
        }
    }

    // ================================================================
    // Repository input guards — zero-chunk metadata corruption
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_rejects_zero_chunk_metadata_with_actual_rows()
    {
        await using var seedDb = _fixture.CreateContext();

        var orgId = Guid.NewGuid();
        seedDb.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = "ZeroCorruptOrg-" + orgId.ToString("N")[..8],
            Slug = "zero-corrupt-org-" + orgId.ToString("N")[..8],
            CreatedAt = Timestamp,
        });
        await seedDb.SaveChangesAsync();

        var projectId = Guid.NewGuid();
        seedDb.Projects.Add(new Project(projectId, orgId, "P", null, Timestamp));
        await seedDb.SaveChangesAsync();

        var documentId = Guid.NewGuid();
        seedDb.Documents.Add(new Document(documentId, orgId, projectId, "f.txt", "text/plain", 10, Timestamp));
        await seedDb.SaveChangesAsync();

        seedDb.DocumentExtractions.Add(new DocumentExtraction(documentId, "orphan", Timestamp));
        await seedDb.SaveChangesAsync();

        seedDb.DocumentChunkSets.Add(new DocumentChunkSet(documentId, 1, 0, Timestamp));
        seedDb.DocumentChunks.Add(new DocumentChunk(Guid.NewGuid(), documentId, 0, 0, 6, "orphan"));
        await seedDb.SaveChangesAsync();

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentEmbeddingSetRepository(db);
            var embeddingSet = new DocumentEmbeddingSet(
                Guid.NewGuid(), documentId, 1, "opsflow-semantic-v1",
                "model", 1536, 0, Timestamp);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repo.AddIfAbsentAsync(embeddingSet, [], projectId, orgId, CancellationToken.None));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Empty(await db.DocumentEmbeddingSets
                .AsNoTracking()
                .Where(es => es.DocumentId == documentId)
                .ToListAsync());
        }
    }
}
