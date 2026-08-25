namespace OpsFlow.Application.Documents;

/// <summary>Input for the extract-document-text use case (POST).</summary>
/// <param name="OrganizationId">The authenticated caller's organization (from JWT).</param>
/// <param name="ProjectId">The project the document belongs to.</param>
/// <param name="DocumentId">The document whose text should be extracted.</param>
public sealed record ExtractDocumentTextCommand(Guid OrganizationId, Guid ProjectId, Guid DocumentId);
