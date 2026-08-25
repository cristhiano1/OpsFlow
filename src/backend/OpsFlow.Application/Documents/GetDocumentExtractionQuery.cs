namespace OpsFlow.Application.Documents;

/// <summary>Input for the get-document-extraction use case (GET, read-only).</summary>
/// <param name="OrganizationId">The authenticated caller's organization (from JWT).</param>
/// <param name="ProjectId">The project the document belongs to.</param>
/// <param name="DocumentId">The document whose extraction is requested.</param>
public sealed record GetDocumentExtractionQuery(Guid OrganizationId, Guid ProjectId, Guid DocumentId);
