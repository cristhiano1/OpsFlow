namespace OpsFlow.Application.Documents;

/// <summary>
/// Read-only snapshot of a document's chunk set and its ordered chunks.
/// Used by the embedding service to generate embeddings without coupling
/// to the chunk-set repository.
/// </summary>
/// <param name="DocumentId">The document these chunks belong to.</param>
/// <param name="ChunkingVersion">Algorithm version that produced the chunks.</param>
/// <param name="ChunkCount">Total chunk count from the chunk set metadata.</param>
/// <param name="Chunks">Ordered chunk sources (by ChunkIndex).</param>
public sealed record DocumentChunkSnapshot(
    Guid DocumentId,
    int ChunkingVersion,
    int ChunkCount,
    IReadOnlyList<DocumentChunkSource> Chunks);
