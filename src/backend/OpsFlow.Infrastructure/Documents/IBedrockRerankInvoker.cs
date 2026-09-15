namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// Narrow, provider-neutral seam over the single Bedrock rerank operation. It
/// exists so the AWS SDK request/response and exception types are confined to
/// its production implementation and never surface in the reranker adapter, the
/// Application layer, or tests. Inputs and outputs are plain values: a query
/// string, ordered candidate document texts, and index/score pairs. No AWS SDK
/// type crosses this boundary.
/// </summary>
internal interface IBedrockRerankInvoker
{
    /// <summary>
    /// Scores the supplied <paramref name="documents"/> against
    /// <paramref name="query"/> via Bedrock. Documents are passed in caller order;
    /// each returned <see cref="BedrockRerankResult.Index"/> refers back to that
    /// order. Implementations translate provider/transport failures into
    /// <see cref="OpsFlow.Application.Documents.ChunkRerankingException"/> and let
    /// caller-requested cancellation propagate unchanged.
    /// </summary>
    Task<IReadOnlyList<BedrockRerankResult>> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken);
}

/// <summary>
/// A single provider-neutral rerank result: the zero-based index of the scored
/// document in the request order, and its relevance score. Correlation back to a
/// <c>DocumentChunkId</c> is performed locally by the adapter, never by the
/// provider.
/// </summary>
/// <param name="Index">Zero-based index into the request's document list.</param>
/// <param name="RelevanceScore">Provider relevance score; higher is more relevant.</param>
internal readonly record struct BedrockRerankResult(int Index, double RelevanceScore);
