namespace OpsFlow.Application.Documents;

/// <summary>
/// A reranker's relevance score for one candidate. The provider supplies only a
/// chunk identifier (echoed from the supplied candidate set for correlation) and
/// a relevance score. The score must be a finite number; the Application layer
/// requires only relative ordering and does not assume any particular scale
/// (for example 0..1) or normalize scores.
/// </summary>
/// <param name="DocumentChunkId">Identifier of the scored candidate; must be one of the supplied candidates.</param>
/// <param name="RelevanceScore">Relevance score; higher means more relevant. Must be finite.</param>
public sealed record ChunkRerankScore(
    Guid DocumentChunkId,
    double RelevanceScore);
