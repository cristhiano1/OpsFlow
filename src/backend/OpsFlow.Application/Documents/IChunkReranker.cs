namespace OpsFlow.Application.Documents;

/// <summary>
/// Provider-neutral port for reranking already-retrieved chunk candidates.
/// Implementations reside in Infrastructure. The reranker receives the query and
/// candidate texts and returns one relevance score per candidate; it never
/// authors authoritative chunk metadata and never controls final ordering —
/// the Application layer validates the scores and applies deterministic
/// ordering.
/// </summary>
public interface IChunkReranker
{
    /// <summary>Immutable identity: profile, model, and maximum candidate count.</summary>
    RerankerIdentity Identity { get; }

    /// <summary>
    /// Scores every supplied candidate for relevance to the query. Implementations
    /// must return exactly one finite score per candidate; the returned order is
    /// not significant.
    /// </summary>
    Task<IReadOnlyList<ChunkRerankScore>> RerankAsync(
        ChunkRerankRequest request,
        CancellationToken cancellationToken);
}
