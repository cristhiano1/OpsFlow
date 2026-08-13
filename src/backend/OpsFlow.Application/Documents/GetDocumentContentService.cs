namespace OpsFlow.Application.Documents;

/// <summary>
/// Coordinates the get-document-content use case: performs a tenant-scoped
/// metadata lookup, opens the storage stream, and distinguishes not-found
/// from storage-missing outcomes.
/// </summary>
public sealed class GetDocumentContentService
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentStorage _documentStorage;

    /// <summary>Creates the service with its repository and storage dependencies.</summary>
    public GetDocumentContentService(
        IDocumentRepository documentRepository,
        IDocumentStorage documentStorage)
    {
        ArgumentNullException.ThrowIfNull(documentRepository);
        ArgumentNullException.ThrowIfNull(documentStorage);

        _documentRepository = documentRepository;
        _documentStorage = documentStorage;
    }

    /// <summary>
    /// Retrieves the document content stream for the specified document within
    /// the caller's organization and project scope.
    /// </summary>
    public async Task<GetDocumentContentResult> GetAsync(
        GetDocumentContentQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization ID must not be empty.", nameof(query));
        }

        if (query.ProjectId == Guid.Empty)
        {
            return GetDocumentContentResult.NotFound();
        }

        if (query.DocumentId == Guid.Empty)
        {
            return GetDocumentContentResult.NotFound();
        }

        var document = await _documentRepository.GetByProjectAsync(
            query.DocumentId, query.ProjectId, query.OrganizationId, cancellationToken);

        if (document is null)
        {
            return GetDocumentContentResult.NotFound();
        }

        var address = new DocumentStorageAddress(
            document.OrganizationId, document.ProjectId, document.Id);

        var stream = await _documentStorage.OpenReadAsync(address, cancellationToken);

        if (stream is null)
        {
            return GetDocumentContentResult.StorageMissing();
        }

        return GetDocumentContentResult.Success(document, stream);
    }
}
