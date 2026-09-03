using System.Text;
using OpsFlow.Application.Projects;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Coordinates the lexical search-document-chunks use case: validates input,
/// checks project existence, and delegates to the lexical retriever for
/// full-text ranked results.
/// </summary>
public sealed class SearchDocumentChunksLexicallyService
{
    private const int MaxQueryTextLength = 2500;
    private const int MinTopK = 1;
    private const int MaxTopK = 50;

    private readonly IProjectRepository _projectRepository;
    private readonly ILexicalChunkRetriever _retriever;

    /// <summary>Creates the service with its dependencies.</summary>
    public SearchDocumentChunksLexicallyService(
        IProjectRepository projectRepository,
        ILexicalChunkRetriever retriever)
    {
        ArgumentNullException.ThrowIfNull(projectRepository);
        ArgumentNullException.ThrowIfNull(retriever);

        _projectRepository = projectRepository;
        _retriever = retriever;
    }

    /// <summary>Searches for document chunks matching the query text lexically.</summary>
    public async Task<SearchDocumentChunksLexicallyResult> SearchAsync(
        SearchDocumentChunksLexicallyQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization ID must not be empty.", nameof(query));
        }

        if (query.ProjectId == Guid.Empty)
        {
            return SearchDocumentChunksLexicallyResult.ProjectNotFound();
        }

        ArgumentNullException.ThrowIfNull(query.QueryText);

        if (string.IsNullOrWhiteSpace(query.QueryText))
        {
            throw new ArgumentException("Query text must not be empty or whitespace.", nameof(query));
        }

        if (query.QueryText.Length > MaxQueryTextLength)
        {
            throw new ArgumentException(
                $"Query text length ({query.QueryText.Length}) exceeds maximum ({MaxQueryTextLength}).",
                nameof(query));
        }

        if (!query.QueryText.EnumerateRunes().Any(Rune.IsLetterOrDigit))
        {
            throw new ArgumentException(
                "Query text must contain at least one letter or digit.",
                nameof(query));
        }

        if (query.TopK is < MinTopK or > MaxTopK)
        {
            throw new ArgumentOutOfRangeException(nameof(query),
                $"TopK must be between {MinTopK} and {MaxTopK}, but was {query.TopK}.");
        }

        var exists = await _projectRepository.ExistsInOrganizationAsync(
            query.ProjectId, query.OrganizationId, cancellationToken);

        if (!exists)
        {
            return SearchDocumentChunksLexicallyResult.ProjectNotFound();
        }

        var hits = await _retriever.RetrieveAsync(
            query.OrganizationId,
            query.ProjectId,
            query.QueryText,
            query.TopK,
            cancellationToken);

        return SearchDocumentChunksLexicallyResult.Success(hits);
    }
}
