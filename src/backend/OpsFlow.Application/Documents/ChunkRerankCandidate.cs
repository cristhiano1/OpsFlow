namespace OpsFlow.Application.Documents;

/// <summary>
/// A single candidate presented to a reranker. Carries only what a reranker
/// legitimately needs: the chunk's identity (for correlating the returned score
/// back to the authoritative <see cref="HybridChunkHit"/>), its 1-based rank in
/// the hybrid result, and its text. No authoritative metadata (DocumentId,
/// ChunkIndex, offsets) and no evaluation data (relevance grades, case ids,
/// expected keys) are exposed to the provider.
/// </summary>
/// <param name="DocumentChunkId">The chunk's identifier; used only to correlate the provider's score.</param>
/// <param name="OriginalHybridRank">1-based rank of this candidate in the hybrid (RRF) result.</param>
/// <param name="Text">The exact chunk text to be scored.</param>
public sealed record ChunkRerankCandidate(
    Guid DocumentChunkId,
    int OriginalHybridRank,
    string Text);
