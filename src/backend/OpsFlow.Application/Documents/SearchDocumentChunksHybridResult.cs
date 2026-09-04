namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of the hybrid search-document-chunks use case. Distinguishes between
/// a project that exists within the caller's organization (with fused hits) and
/// one that does not.
/// </summary>
public sealed class SearchDocumentChunksHybridResult
{
    /// <summary>
    /// <see langword="true"/> when the project was found within the caller's
    /// organization; <see langword="false"/> otherwise.
    /// </summary>
    public bool ProjectFound { get; private set; }

    /// <summary>
    /// Hybrid chunk hits ordered by RRF score descending.
    /// Only meaningful when <see cref="ProjectFound"/> is <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<HybridChunkHit> Hits { get; private set; } = [];

    private SearchDocumentChunksHybridResult() { }

    /// <summary>Creates a successful result containing the fused hits.</summary>
    public static SearchDocumentChunksHybridResult Success(IReadOnlyList<HybridChunkHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        return new SearchDocumentChunksHybridResult { ProjectFound = true, Hits = hits };
    }

    /// <summary>
    /// Creates a not-found result. Used when the project does not exist or
    /// belongs to a different organization.
    /// </summary>
    public static SearchDocumentChunksHybridResult ProjectNotFound() =>
        new() { ProjectFound = false };
}
