using Microsoft.EntityFrameworkCore;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Domain.Documents;
using OpsFlow.Domain.Organizations;
using OpsFlow.Domain.Projects;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Api.IntegrationTests.Documents;

[Collection(SqlServerCollection.Name)]
public sealed class DocumentChunkingIntegrationTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
    private readonly SqlServerFixture _fixture;

    public DocumentChunkingIntegrationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    // ================================================================
    // Helpers
    // ================================================================

    private async Task<(Guid OrgId, Guid ProjectId, Guid DocumentId)> SeedDocumentWithExtractionAsync(
        string extractedText = "Hello world")
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

        db.DocumentExtractions.Add(new DocumentExtraction(documentId, extractedText, Timestamp));
        await db.SaveChangesAsync();

        return (orgId, projectId, documentId);
    }

    // ================================================================
    // Persistence — round-trip
    // ================================================================

    [Fact]
    public async Task ChunkSet_and_chunks_persist_and_read_back()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithExtractionAsync();

        await using (var db = _fixture.CreateContext())
        {
            var chunkSet = new DocumentChunkSet(documentId, 1, 2, Timestamp);
            db.DocumentChunkSets.Add(chunkSet);
            db.DocumentChunks.Add(new DocumentChunk(Guid.NewGuid(), documentId, 0, 0, 5, "Hello"));
            db.DocumentChunks.Add(new DocumentChunk(Guid.NewGuid(), documentId, 1, 3, 11, "lo world"));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var loaded = await db.DocumentChunkSets
                .AsNoTracking()
                .FirstOrDefaultAsync(cs => cs.DocumentId == documentId);

            Assert.NotNull(loaded);
            Assert.Equal(1, loaded.ChunkingVersion);
            Assert.Equal(2, loaded.ChunkCount);
            Assert.Equal(Timestamp, loaded.CreatedAt);

            var chunks = await db.DocumentChunks
                .AsNoTracking()
                .Where(c => c.DocumentId == documentId)
                .OrderBy(c => c.ChunkIndex)
                .ToListAsync();

            Assert.Equal(2, chunks.Count);
            Assert.Equal(0, chunks[0].StartOffset);
            Assert.Equal(5, chunks[0].EndOffset);
            Assert.Equal("Hello", chunks[0].Text);
            Assert.Equal(3, chunks[1].StartOffset);
            Assert.Equal(11, chunks[1].EndOffset);
            Assert.Equal("lo world", chunks[1].Text);
        }
    }

    // ================================================================
    // Cascade — extraction deletion cascades to chunk set and chunks
    // ================================================================

    [Fact]
    public async Task Deleting_extraction_cascades_to_chunk_set_and_chunks()
    {
        var (_, _, documentId) = await SeedDocumentWithExtractionAsync();
        var chunkId = Guid.NewGuid();

        await using (var db = _fixture.CreateContext())
        {
            db.DocumentChunkSets.Add(new DocumentChunkSet(documentId, 1, 1, Timestamp));
            db.DocumentChunks.Add(new DocumentChunk(chunkId, documentId, 0, 0, 11, "Hello world"));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var extraction = await db.DocumentExtractions.FindAsync(documentId);
            Assert.NotNull(extraction);
            db.DocumentExtractions.Remove(extraction);
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Null(await db.DocumentChunkSets.FindAsync(documentId));
            Assert.Null(await db.DocumentChunks.FindAsync(chunkId));
        }
    }

    // ================================================================
    // Cascade — document deletion cascades through entire chain
    // ================================================================

    [Fact]
    public async Task Deleting_document_cascades_through_extraction_to_chunks()
    {
        var (_, _, documentId) = await SeedDocumentWithExtractionAsync();
        var chunkId = Guid.NewGuid();

        await using (var db = _fixture.CreateContext())
        {
            db.DocumentChunkSets.Add(new DocumentChunkSet(documentId, 1, 1, Timestamp));
            db.DocumentChunks.Add(new DocumentChunk(chunkId, documentId, 0, 0, 11, "Hello world"));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var document = await db.Documents.FindAsync(documentId);
            Assert.NotNull(document);
            db.Documents.Remove(document);
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Null(await db.DocumentExtractions.FindAsync(documentId));
            Assert.Null(await db.DocumentChunkSets.FindAsync(documentId));
            Assert.Null(await db.DocumentChunks.FindAsync(chunkId));
        }
    }

    // ================================================================
    // Unique index — duplicate chunk index rejected
    // ================================================================

    [Fact]
    public async Task Duplicate_chunk_index_is_rejected()
    {
        var (_, _, documentId) = await SeedDocumentWithExtractionAsync();

        await using var db = _fixture.CreateContext();
        db.DocumentChunkSets.Add(new DocumentChunkSet(documentId, 1, 2, Timestamp));
        db.DocumentChunks.Add(new DocumentChunk(Guid.NewGuid(), documentId, 0, 0, 5, "Hello"));
        db.DocumentChunks.Add(new DocumentChunk(Guid.NewGuid(), documentId, 0, 5, 11, " world"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ================================================================
    // Repository — AddIfAbsent persists atomically
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_persists_set_and_chunks_atomically()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithExtractionAsync();

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentChunkSetRepository(db);
            var chunkSet = new DocumentChunkSet(documentId, 1, 2, Timestamp);
            var chunks = new List<DocumentChunk>
            {
                new(Guid.NewGuid(), documentId, 0, 0, 5, "Hello"),
                new(Guid.NewGuid(), documentId, 1, 5, 11, " world"),
            };

            var result = await repo.AddIfAbsentAsync(chunkSet, chunks, projectId, orgId, CancellationToken.None);

            Assert.True(result.WasAdded);
        }

        await using (var db = _fixture.CreateContext())
        {
            var loaded = await db.DocumentChunkSets.AsNoTracking().FirstAsync(cs => cs.DocumentId == documentId);
            Assert.Equal(2, loaded.ChunkCount);

            var loadedChunks = await db.DocumentChunks.AsNoTracking()
                .Where(c => c.DocumentId == documentId)
                .OrderBy(c => c.ChunkIndex)
                .ToListAsync();
            Assert.Equal(2, loadedChunks.Count);
        }
    }

    // ================================================================
    // Repository — AddIfAbsent returns AlreadyExists on conflict
    // ================================================================

    [Fact]
    public async Task Repository_AddIfAbsent_returns_AlreadyExists_on_duplicate()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithExtractionAsync();

        await using (var db = _fixture.CreateContext())
        {
            db.DocumentChunkSets.Add(new DocumentChunkSet(documentId, 1, 1, Timestamp));
            db.DocumentChunks.Add(new DocumentChunk(Guid.NewGuid(), documentId, 0, 0, 11, "Hello world"));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentChunkSetRepository(db);
            var chunkSet = new DocumentChunkSet(documentId, 1, 1, Timestamp);
            var chunks = new List<DocumentChunk>
            {
                new(Guid.NewGuid(), documentId, 0, 0, 11, "Hello world"),
            };

            var result = await repo.AddIfAbsentAsync(chunkSet, chunks, projectId, orgId, CancellationToken.None);

            Assert.False(result.WasAdded);
            Assert.Equal(documentId, result.ChunkSet.DocumentId);
        }
    }

    // ================================================================
    // Repository — tenant-scoped read
    // ================================================================

    [Fact]
    public async Task Repository_GetByDocument_returns_null_for_wrong_org()
    {
        var (_, projectId, documentId) = await SeedDocumentWithExtractionAsync();

        await using (var db = _fixture.CreateContext())
        {
            db.DocumentChunkSets.Add(new DocumentChunkSet(documentId, 1, 1, Timestamp));
            db.DocumentChunks.Add(new DocumentChunk(Guid.NewGuid(), documentId, 0, 0, 11, "Hello world"));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentChunkSetRepository(db);
            var wrongOrgId = Guid.NewGuid();
            var result = await repo.GetByDocumentAsync(documentId, projectId, wrongOrgId, CancellationToken.None);

            Assert.Null(result);
        }
    }

    // ================================================================
    // Repository — tenant-scoped read succeeds
    // ================================================================

    [Fact]
    public async Task Repository_GetByDocument_returns_chunk_set_for_correct_tenant()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithExtractionAsync();

        await using (var db = _fixture.CreateContext())
        {
            db.DocumentChunkSets.Add(new DocumentChunkSet(documentId, 1, 1, Timestamp));
            db.DocumentChunks.Add(new DocumentChunk(Guid.NewGuid(), documentId, 0, 0, 11, "Hello world"));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentChunkSetRepository(db);
            var result = await repo.GetByDocumentAsync(documentId, projectId, orgId, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(documentId, result.DocumentId);
            Assert.Equal(1, result.ChunkingVersion);
        }
    }

    // ================================================================
    // Empty extraction — chunk set with zero count
    // ================================================================

    [Fact]
    public async Task Empty_extraction_persists_chunk_set_with_zero_count()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithExtractionAsync(extractedText: string.Empty);

        await using (var db = _fixture.CreateContext())
        {
            var repo = new EfDocumentChunkSetRepository(db);
            var chunkSet = new DocumentChunkSet(documentId, 1, 0, Timestamp);
            var result = await repo.AddIfAbsentAsync(chunkSet, [], projectId, orgId, CancellationToken.None);

            Assert.True(result.WasAdded);
        }

        await using (var db = _fixture.CreateContext())
        {
            var loaded = await db.DocumentChunkSets.AsNoTracking().FirstAsync(cs => cs.DocumentId == documentId);
            Assert.Equal(0, loaded.ChunkCount);

            var chunks = await db.DocumentChunks.AsNoTracking()
                .Where(c => c.DocumentId == documentId)
                .ToListAsync();
            Assert.Empty(chunks);
        }
    }

    // ================================================================
    // Schema — nvarchar(1600) enforced
    // ================================================================

    [Fact]
    public async Task Text_at_max_length_persists_successfully()
    {
        var (orgId, projectId, documentId) = await SeedDocumentWithExtractionAsync(
            extractedText: new string('x', DocumentChunk.MaxTextLength));

        await using var db = _fixture.CreateContext();
        db.DocumentChunkSets.Add(new DocumentChunkSet(documentId, 1, 1, Timestamp));
        var text = new string('x', DocumentChunk.MaxTextLength);
        db.DocumentChunks.Add(new DocumentChunk(Guid.NewGuid(), documentId, 0, 0, text.Length, text));
        await db.SaveChangesAsync();

        var chunk = await db.DocumentChunks.AsNoTracking()
            .FirstAsync(c => c.DocumentId == documentId);
        Assert.Equal(DocumentChunk.MaxTextLength, chunk.Text.Length);
    }
}
