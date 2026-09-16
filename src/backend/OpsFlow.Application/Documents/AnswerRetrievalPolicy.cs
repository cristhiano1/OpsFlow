namespace OpsFlow.Application.Documents;

/// <summary>
/// Provider-neutral policy that selects how the grounded-answer path retrieves
/// evidence. The Application layer takes no dependency on any configuration or
/// options framework; the composition root maps a configuration value onto this
/// value and supplies it to <see cref="AnswerProjectQuestionService"/>.
/// </summary>
public enum AnswerRetrievalPolicy
{
    /// <summary>Retrieve evidence with hybrid RRF only (reranking not activated).</summary>
    HybridOnly = 0,

    /// <summary>
    /// Retrieve evidence via the reranked path, falling back to hybrid RRF only
    /// when the reranker is operationally unavailable
    /// (<see cref="ChunkRerankingException"/>). Malformed/untrusted reranker output
    /// fails closed and never triggers fallback.
    /// </summary>
    RerankWithHybridFallback = 1,
}
