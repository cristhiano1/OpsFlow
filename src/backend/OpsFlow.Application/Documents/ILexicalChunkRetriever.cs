namespace OpsFlow.Application.Documents;

/// <summary>
/// Provider-neutral port for project-scoped lexical chunk retrieval.
/// Infrastructure provides the SQL Server Full-Text Search implementation.
/// </summary>
public interface ILexicalChunkRetriever
{
    /// <summary>
    /// Retrieves document chunks matching the search text within a single
    /// project, ranked by full-text relevance descending.
    /// </summary>
    Task<IReadOnlyList<LexicalChunkHit>> RetrieveAsync(
        Guid organizationId,
        Guid projectId,
        string queryText,
        int topK,
        CancellationToken cancellationToken);
}
