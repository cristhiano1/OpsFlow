using Microsoft.Extensions.Options;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Configuration;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// Amazon Bedrock-backed <see cref="IChunkReranker"/>. It sends only the query
/// and candidate texts to Bedrock (via the provider-neutral
/// <see cref="IBedrockRerankInvoker"/> seam), correlates each returned score back
/// to a <c>DocumentChunkId</c> locally by request index, and returns one score
/// per correlated result. It never sends tenant/document metadata, never reorders
/// or truncates, and never repairs malformed output: the Application layer
/// (<see cref="SearchDocumentChunksRerankedService"/>) remains authoritative for
/// count, duplicate, missing, and finiteness validation and for final ordering.
/// This adapter contains no AWS SDK types and is safe for singleton lifetime.
/// It is internal (registered via the public <see cref="IChunkReranker"/> port)
/// so the internal Bedrock seam does not leak into its public surface.
/// </summary>
internal sealed class BedrockChunkReranker : IChunkReranker
{
    /// <summary>Product-level reranking profile identifier.</summary>
    public const string ProfileId = "opsflow-rerank-v1";

    /// <summary>
    /// OpsFlow's operational reranking capacity — the maximum candidates this
    /// adapter will submit in one request. This is an OpsFlow policy boundary
    /// (chosen so a single rerank stays within one Bedrock/Cohere search-unit
    /// under ordinary document sizing), not a claimed Bedrock hard API limit. It
    /// comfortably exceeds the retrieval pool's ceiling of 50.
    /// </summary>
    public const int MaxCandidates = 100;

    private readonly IBedrockRerankInvoker _invoker;

    /// <inheritdoc />
    public RerankerIdentity Identity { get; }

    /// <summary>Creates the reranker over the Bedrock invoker seam.</summary>
    public BedrockChunkReranker(
        IBedrockRerankInvoker invoker,
        IOptions<BedrockRerankerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(options);

        _invoker = invoker;
        Identity = new RerankerIdentity(ProfileId, options.Value.ModelId, MaxCandidates);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChunkRerankScore>> RerankAsync(
        ChunkRerankRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var candidates = request.Candidates;
        if (candidates is null || candidates.Count == 0)
        {
            // No candidates: never call the provider.
            return [];
        }

        // Send only candidate text, in the exact candidate order. The provider
        // never receives DocumentChunkId or any other metadata.
        var documents = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            documents.Add(candidate.Text);
        }

        var results = await _invoker.RerankAsync(request.Query, documents, cancellationToken);

        // Correlate each provider result back to a DocumentChunkId locally, by
        // request index. Reject an index that cannot be correlated rather than
        // invent an id. Do not sort, deduplicate, fill, repair, normalize, or
        // clamp — the Application layer validates the full score set and owns the
        // final ordering.
        var scores = new List<ChunkRerankScore>(results.Count);
        foreach (var result in results)
        {
            if (result.Index < 0 || result.Index >= candidates.Count)
            {
                throw new ChunkRerankingException(
                    $"Bedrock returned an out-of-range result index {result.Index} for " +
                    $"{candidates.Count} candidates.");
            }

            scores.Add(new ChunkRerankScore(
                candidates[result.Index].DocumentChunkId,
                result.RelevanceScore));
        }

        return scores;
    }
}
