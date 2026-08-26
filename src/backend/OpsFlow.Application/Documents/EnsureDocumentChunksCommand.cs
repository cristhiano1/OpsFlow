namespace OpsFlow.Application.Documents;

/// <summary>Input for the ensure-document-chunks use case.</summary>
/// <param name="OrganizationId">The authenticated caller's organization (from JWT).</param>
/// <param name="ProjectId">The project the document belongs to.</param>
/// <param name="DocumentId">The document whose chunks should be ensured.</param>
public sealed record EnsureDocumentChunksCommand(Guid OrganizationId, Guid ProjectId, Guid DocumentId);
