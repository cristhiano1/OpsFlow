using System.Text;
using OpsFlow.Application.Projects;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Coordinates the hybrid search-document-chunks use case: validates input,
/// embeds the query text, retrieves from both semantic and lexical sources
/// sequentially, and fuses the results via Reciprocal Rank Fusion.
/// </summary>
public sealed class SearchDocumentChunksHybridService
{
    private const int MaxQueryTextLength = 2500;
    private const int MinTopK = 1;
    private const int MaxTopK = 50;
    private const int CandidateDepth = 50;

    private readonly IProjectRepository _projectRepository;
    private readonly IEmbeddingGenerator _generator;
    private readonly ISemanticChunkRetriever _semanticRetriever;
    private readonly ILexicalChunkRetriever _lexicalRetriever;

    /// <summary>Creates the service with its dependencies.</summary>
    public SearchDocumentChunksHybridService(
        IProjectRepository projectRepository,
        IEmbeddingGenerator generator,
        ISemanticChunkRetriever semanticRetriever,
        ILexicalChunkRetriever lexicalRetriever)
    {
        ArgumentNullException.ThrowIfNull(projectRepository);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(semanticRetriever);
        ArgumentNullException.ThrowIfNull(lexicalRetriever);

        _projectRepository = projectRepository;
        _generator = generator;
        _semanticRetriever = semanticRetriever;
        _lexicalRetriever = lexicalRetriever;
    }

    /// <summary>Searches for document chunks using hybrid semantic + lexical retrieval with RRF fusion.</summary>
    public async Task<SearchDocumentChunksHybridResult> SearchAsync(
        SearchDocumentChunksHybridQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization ID must not be empty.", nameof(query));
        }

        if (query.ProjectId == Guid.Empty)
        {
            return SearchDocumentChunksHybridResult.ProjectNotFound();
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

        ValidateGeneratorIdentity();

        var exists = await _projectRepository.ExistsInOrganizationAsync(
            query.ProjectId, query.OrganizationId, cancellationToken);

        if (!exists)
        {
            return SearchDocumentChunksHybridResult.ProjectNotFound();
        }

        var vectors = await _generator.GenerateAsync(
            [query.QueryText], cancellationToken);

        ValidateGeneratorOutput(vectors);

        var semanticHits = await _semanticRetriever.RetrieveAsync(
            query.OrganizationId,
            query.ProjectId,
            _generator.Identity,
            vectors[0],
            CandidateDepth,
            cancellationToken);

        var lexicalHits = await _lexicalRetriever.RetrieveAsync(
            query.OrganizationId,
            query.ProjectId,
            query.QueryText,
            CandidateDepth,
            cancellationToken);

        var fusedHits = ReciprocalRankFusion.Fuse(semanticHits, lexicalHits, query.TopK);

        return SearchDocumentChunksHybridResult.Success(fusedHits);
    }

    private void ValidateGeneratorIdentity()
    {
        var identity = _generator.Identity;

        if (identity.ProfileId != EmbeddingProfiles.SemanticV1Id)
        {
            throw new InvalidOperationException(
                $"Generator profile '{identity.ProfileId}' is not the supported profile " +
                $"'{EmbeddingProfiles.SemanticV1Id}'.");
        }

        if (identity.ModelId != EmbeddingProfiles.SemanticV1ModelId)
        {
            throw new InvalidOperationException(
                $"Generator model '{identity.ModelId}' is not the supported model " +
                $"'{EmbeddingProfiles.SemanticV1ModelId}'.");
        }

        if (identity.Dimensions != EmbeddingProfiles.SemanticV1Dimensions)
        {
            throw new InvalidOperationException(
                $"Generator dimensions {identity.Dimensions} do not match the supported " +
                $"profile dimensions {EmbeddingProfiles.SemanticV1Dimensions}.");
        }
    }

    private void ValidateGeneratorOutput(IReadOnlyList<ReadOnlyMemory<float>> vectors)
    {
        if (vectors.Count != 1)
        {
            throw new InvalidOperationException(
                $"Generator returned {vectors.Count} vectors but expected 1.");
        }

        var vector = vectors[0];

        if (vector.Length != _generator.Identity.Dimensions)
        {
            throw new InvalidOperationException(
                $"Query vector has {vector.Length} dimensions but expected {_generator.Identity.Dimensions}.");
        }

        var span = vector.Span;
        bool anyNonZero = false;
        for (int i = 0; i < span.Length; i++)
        {
            if (!float.IsFinite(span[i]))
            {
                throw new InvalidOperationException(
                    $"Query vector contains non-finite value {span[i]} at component {i}.");
            }

            if (span[i] != 0f)
            {
                anyNonZero = true;
            }
        }

        if (!anyNonZero)
        {
            throw new InvalidOperationException(
                "Query vector has zero norm and cannot be used for cosine distance retrieval.");
        }
    }
}
