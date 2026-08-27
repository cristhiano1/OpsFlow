namespace OpsFlow.Application.Documents;

/// <summary>
/// A single chunk from a snapshot, carrying only the fields needed for
/// embedding generation: identity, position, and text content.
/// </summary>
/// <param name="ChunkId">The chunk's unique identifier.</param>
/// <param name="ChunkIndex">Zero-based position within the chunk set.</param>
/// <param name="Text">The chunk text content.</param>
public sealed record DocumentChunkSource(Guid ChunkId, int ChunkIndex, string Text);
