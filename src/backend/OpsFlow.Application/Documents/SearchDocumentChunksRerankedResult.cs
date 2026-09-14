namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of the reranked search-document-chunks use case. Mirrors
/// <see cref="SearchDocumentChunksHybridResult"/>: distinguishes a project that
/// exists within the caller's organization (with reranked hits) from one that
/// does not.
/// </summary>
public sealed class SearchDocumentChunksRerankedResult
{
    /// <summary>
    /// <see langword="true"/> when the project was found within the caller's
    /// organization; <see langword="false"/> otherwise.
    /// </summary>
    public bool ProjectFound { get; private set; }

    /// <summary>
    /// Reranked chunk hits ordered by reranker relevance descending (with
    /// deterministic tie-breaks). Only meaningful when <see cref="ProjectFound"/>
    /// is <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<RerankedChunkHit> Hits { get; private set; } = [];

    private SearchDocumentChunksRerankedResult() { }

    /// <summary>Creates a successful result containing the reranked hits.</summary>
    public static SearchDocumentChunksRerankedResult Success(IReadOnlyList<RerankedChunkHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        return new SearchDocumentChunksRerankedResult { ProjectFound = true, Hits = hits };
    }

    /// <summary>
    /// Creates a not-found result. Used when the project does not exist or
    /// belongs to a different organization.
    /// </summary>
    public static SearchDocumentChunksRerankedResult ProjectNotFound() =>
        new() { ProjectFound = false };
}
