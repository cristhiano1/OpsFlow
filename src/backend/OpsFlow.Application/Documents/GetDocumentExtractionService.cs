namespace OpsFlow.Application.Documents;

/// <summary>
/// Coordinates the get-document-extraction use case (GET): performs a tenant-
/// scoped lookup of a persisted extraction. Never opens storage, never runs
/// a parser, never modifies the database.
/// </summary>
public sealed class GetDocumentExtractionService
{
    private readonly IDocumentExtractionRepository _extractionRepository;

    /// <summary>Creates the service with its extraction repository dependency.</summary>
    public GetDocumentExtractionService(IDocumentExtractionRepository extractionRepository)
    {
        ArgumentNullException.ThrowIfNull(extractionRepository);
        _extractionRepository = extractionRepository;
    }

    /// <summary>Reads a previously persisted extraction (read-only, never triggers extraction).</summary>
    public async Task<GetDocumentExtractionResult> GetAsync(
        GetDocumentExtractionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization ID must not be empty.", nameof(query));
        }

        if (query.ProjectId == Guid.Empty)
        {
            return GetDocumentExtractionResult.NotFound();
        }

        if (query.DocumentId == Guid.Empty)
        {
            return GetDocumentExtractionResult.NotFound();
        }

        var extraction = await _extractionRepository.GetByDocumentAsync(
            query.DocumentId, query.ProjectId, query.OrganizationId, cancellationToken);

        return extraction is not null
            ? GetDocumentExtractionResult.Success(extraction)
            : GetDocumentExtractionResult.NotFound();
    }
}
