using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;
using OpsFlow.Infrastructure.Documents;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.UnitTests.Documents;

public sealed class EfDocumentEmbeddingSetRepositoryTests
{
    private static OpsFlowDbContext CreateUnreachableContext()
    {
        var options = new DbContextOptionsBuilder<OpsFlowDbContext>()
            .UseSqlServer("Server=invalid;Database=invalid;Encrypt=false")
            .Options;
        return new OpsFlowDbContext(options);
    }

    [Fact]
    public async Task AddIfAbsentAsync_rejects_zero_norm_embedding()
    {
        await using var db = CreateUnreachableContext();
        var repository = new EfDocumentEmbeddingSetRepository(db);

        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();

        var embeddingSet = new DocumentEmbeddingSet(
            Guid.NewGuid(),
            documentId,
            chunkingVersion: 1,
            EmbeddingProfiles.SemanticV1Id,
            EmbeddingProfiles.SemanticV1ModelId,
            EmbeddingProfiles.SemanticV1Dimensions,
            embeddingCount: 1,
            DateTimeOffset.UtcNow);

        var zeroVector = new float[EmbeddingProfiles.SemanticV1Dimensions];
        var embeddings = new[] { new ChunkEmbeddingInput(chunkId, zeroVector) };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.AddIfAbsentAsync(
                embeddingSet, embeddings, Guid.NewGuid(), Guid.NewGuid(),
                CancellationToken.None));

        Assert.Contains("zero norm", ex.Message);
        Assert.Equal("embeddings", ex.ParamName);
    }

    [Fact]
    public async Task AddIfAbsentAsync_rejects_negative_zero_only_embedding()
    {
        await using var db = CreateUnreachableContext();
        var repository = new EfDocumentEmbeddingSetRepository(db);

        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();

        var embeddingSet = new DocumentEmbeddingSet(
            Guid.NewGuid(),
            documentId,
            chunkingVersion: 1,
            EmbeddingProfiles.SemanticV1Id,
            EmbeddingProfiles.SemanticV1ModelId,
            EmbeddingProfiles.SemanticV1Dimensions,
            embeddingCount: 1,
            DateTimeOffset.UtcNow);

        var negZeroVector = new float[EmbeddingProfiles.SemanticV1Dimensions];
        negZeroVector[0] = -0.0f;

        var embeddings = new[] { new ChunkEmbeddingInput(chunkId, negZeroVector) };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.AddIfAbsentAsync(
                embeddingSet, embeddings, Guid.NewGuid(), Guid.NewGuid(),
                CancellationToken.None));

        Assert.Contains("zero norm", ex.Message);
    }

    [Fact]
    public async Task AddIfAbsentAsync_accepts_vector_with_float_epsilon()
    {
        await using var db = CreateUnreachableContext();
        var repository = new EfDocumentEmbeddingSetRepository(db);

        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();

        var embeddingSet = new DocumentEmbeddingSet(
            Guid.NewGuid(),
            documentId,
            chunkingVersion: 1,
            EmbeddingProfiles.SemanticV1Id,
            EmbeddingProfiles.SemanticV1ModelId,
            EmbeddingProfiles.SemanticV1Dimensions,
            embeddingCount: 1,
            DateTimeOffset.UtcNow);

        var epsilonVector = new float[EmbeddingProfiles.SemanticV1Dimensions];
        epsilonVector[0] = float.Epsilon;

        var embeddings = new[] { new ChunkEmbeddingInput(chunkId, epsilonVector) };

        // Should pass zero-norm validation but fail later when hitting the
        // unreachable database. InvalidOperationException from EF confirms
        // the vector validation itself did not reject the input.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AddIfAbsentAsync(
                embeddingSet, embeddings, Guid.NewGuid(), Guid.NewGuid(),
                CancellationToken.None));
    }
}
