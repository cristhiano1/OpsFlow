namespace OpsFlow.Application.Documents;

/// <summary>
/// Immutable identity of a chunk-reranking provider. Analogous to
/// <see cref="EmbeddingGeneratorIdentity"/>: it declares the provider's profile,
/// model, and the maximum number of candidates it can score in one request. The
/// Application layer validates this identity before invoking the provider so a
/// misconfigured adapter fails fast rather than silently.
/// </summary>
/// <param name="ProfileId">Product-level reranking profile identifier.</param>
/// <param name="ModelId">Provider/model identifier used for the reranking.</param>
/// <param name="MaxCandidates">Maximum candidates the provider can score in a single request (must be &gt; 0).</param>
public sealed record RerankerIdentity(
    string ProfileId,
    string ModelId,
    int MaxCandidates);
