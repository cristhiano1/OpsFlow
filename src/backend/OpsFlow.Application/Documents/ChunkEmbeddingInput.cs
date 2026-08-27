namespace OpsFlow.Application.Documents;

/// <summary>
/// A single chunk's embedding vector, ready for persistence. The vector is
/// provider-neutral (<see cref="ReadOnlyMemory{T}"/> of float); Infrastructure
/// converts to <c>SqlVector&lt;float&gt;</c> at the persistence boundary.
/// </summary>
/// <param name="DocumentChunkId">The chunk this embedding belongs to.</param>
/// <param name="Vector">The embedding vector (float32 components).</param>
public sealed record ChunkEmbeddingInput(Guid DocumentChunkId, ReadOnlyMemory<float> Vector);
