namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of the search-document-chunks use case. Distinguishes between a
/// project that exists within the caller's organization (with hits) and one
/// that does not.
/// </summary>
public sealed class SearchDocumentChunksResult
{
    /// <summary>
    /// <see langword="true"/> when the project was found within the caller's
    /// organization; <see langword="false"/> otherwise.
    /// </summary>
    public bool ProjectFound { get; private set; }

    /// <summary>
    /// Semantic chunk hits ordered by cosine distance ascending.
    /// Only meaningful when <see cref="ProjectFound"/> is <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<SemanticChunkHit> Hits { get; private set; } = [];

    private SearchDocumentChunksResult() { }

    /// <summary>Creates a successful result containing the returned hits.</summary>
    public static SearchDocumentChunksResult Success(IReadOnlyList<SemanticChunkHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        return new SearchDocumentChunksResult { ProjectFound = true, Hits = hits };
    }

    /// <summary>
    /// Creates a not-found result. Used when the project does not exist or
    /// belongs to a different organization.
    /// </summary>
    public static SearchDocumentChunksResult ProjectNotFound() =>
        new() { ProjectFound = false };
}
