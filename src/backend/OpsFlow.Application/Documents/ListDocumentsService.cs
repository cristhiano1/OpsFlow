using OpsFlow.Application.Projects;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Coordinates the list-documents use case. Verifies project ownership within
/// the authenticated organization before returning any document metadata.
/// A project belonging to another organization is intentionally indistinguishable
/// from a nonexistent project.
/// </summary>
public sealed class ListDocumentsService
{
    private readonly IProjectRepository _projectRepository;
    private readonly IDocumentRepository _documentRepository;

    /// <summary>Creates the service with its repository dependencies.</summary>
    public ListDocumentsService(
        IProjectRepository projectRepository,
        IDocumentRepository documentRepository)
    {
        ArgumentNullException.ThrowIfNull(projectRepository);
        ArgumentNullException.ThrowIfNull(documentRepository);
        _projectRepository = projectRepository;
        _documentRepository = documentRepository;
    }

    /// <summary>
    /// Returns the documents for the requested project, or
    /// <see cref="ListDocumentsResult.ProjectNotFound()"/> if the project does
    /// not exist within the caller's organization.
    /// </summary>
    public async Task<ListDocumentsResult> ListAsync(
        ListDocumentsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization ID must not be empty.", nameof(query));
        }

        if (query.ProjectId == Guid.Empty)
        {
            return ListDocumentsResult.ProjectNotFound();
        }

        var exists = await _projectRepository.ExistsInOrganizationAsync(
            query.ProjectId, query.OrganizationId, cancellationToken);

        if (!exists)
        {
            return ListDocumentsResult.ProjectNotFound();
        }

        var documents = await _documentRepository.ListByProjectAsync(
            query.ProjectId, query.OrganizationId, cancellationToken);

        return ListDocumentsResult.Success(documents);
    }
}
