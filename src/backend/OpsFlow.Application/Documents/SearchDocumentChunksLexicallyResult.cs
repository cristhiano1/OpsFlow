namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of the lexical search-document-chunks use case. Distinguishes
/// between a project that exists within the caller's organization (with hits)
/// and one that does not.
/// </summary>
public sealed class SearchDocumentChunksLexicallyResult
{
    /// <summary>
    /// <see langword="true"/> when the project was found within the caller's
    /// organization; <see langword="false"/> otherwise.
    /// </summary>
    public bool ProjectFound { get; private set; }

    /// <summary>
    /// Lexical chunk hits ordered by FTS rank descending.
    /// Only meaningful when <see cref="ProjectFound"/> is <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<LexicalChunkHit> Hits { get; private set; } = [];

    private SearchDocumentChunksLexicallyResult() { }

    /// <summary>Creates a successful result containing the returned hits.</summary>
    public static SearchDocumentChunksLexicallyResult Success(IReadOnlyList<LexicalChunkHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        return new SearchDocumentChunksLexicallyResult { ProjectFound = true, Hits = hits };
    }

    /// <summary>
    /// Creates a not-found result. Used when the project does not exist or
    /// belongs to a different organization.
    /// </summary>
    public static SearchDocumentChunksLexicallyResult ProjectNotFound() =>
        new() { ProjectFound = false };
}
