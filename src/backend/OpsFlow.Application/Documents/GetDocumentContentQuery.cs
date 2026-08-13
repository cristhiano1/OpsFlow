namespace OpsFlow.Application.Documents;

/// <summary>Input for the get-document-content use case.</summary>
/// <param name="OrganizationId">The authenticated caller's organization (from JWT).</param>
/// <param name="ProjectId">The project the document belongs to.</param>
/// <param name="DocumentId">The document whose content is requested.</param>
public sealed record GetDocumentContentQuery(Guid OrganizationId, Guid ProjectId, Guid DocumentId);
