namespace OpsFlow.Application.Documents;

/// <summary>
/// Input to a reranker: the original user query and the ordered candidate
/// chunks to score. The query is forwarded exactly as the user issued it (no
/// trimming, rewriting, or normalization). Candidates are in hybrid (RRF) rank
/// order.
/// </summary>
/// <param name="Query">The exact user query text.</param>
/// <param name="Candidates">Candidate chunks to score, in hybrid rank order.</param>
public sealed record ChunkRerankRequest(
    string Query,
    IReadOnlyList<ChunkRerankCandidate> Candidates);
