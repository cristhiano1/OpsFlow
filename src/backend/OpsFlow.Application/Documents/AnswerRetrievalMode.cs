namespace OpsFlow.Application.Documents;

/// <summary>
/// Provider-neutral record of how evidence was actually retrieved for a grounded
/// answer. It exposes whether reranking was applied without leaking any provider,
/// model, or transport detail (see ADR-009/ADR-011). It is Application/observability
/// metadata only and is intentionally not part of the public HTTP contract.
/// </summary>
public enum AnswerRetrievalMode
{
    /// <summary>
    /// No evidence-retrieval path produced answerable evidence: the project was
    /// not found, or retrieval yielded zero candidates before any reranker scored.
    /// </summary>
    NotApplicable = 0,

    /// <summary>Hybrid-only retrieval supplied the evidence.</summary>
    Hybrid = 1,

    /// <summary>
    /// The reranker actually scored the candidates and reranked evidence supplied
    /// the answer path. This means reranking completed — not merely that the
    /// feature was enabled.
    /// </summary>
    Reranked = 2,

    /// <summary>
    /// Reranking was attempted, an operational <see cref="ChunkRerankingException"/>
    /// occurred, and hybrid fallback supplied the evidence.
    /// </summary>
    HybridFallback = 3,
}
