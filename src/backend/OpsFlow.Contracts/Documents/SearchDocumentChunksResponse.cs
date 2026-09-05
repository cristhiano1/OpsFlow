namespace OpsFlow.Contracts.Documents;

/// <summary>
/// Stable API envelope for a collection of search result hits, ordered by
/// relevance descending.
/// </summary>
/// <param name="Items">The matching document chunks, most relevant first.</param>
public sealed record SearchDocumentChunksResponse(IReadOnlyList<SearchDocumentChunkHitResponse> Items);
