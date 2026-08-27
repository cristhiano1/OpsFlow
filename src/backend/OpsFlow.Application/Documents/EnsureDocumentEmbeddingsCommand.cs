namespace OpsFlow.Application.Documents;

/// <summary>Command for the ensure-document-embeddings use case.</summary>
/// <param name="OrganizationId">Authenticated tenant (from JWT only).</param>
/// <param name="ProjectId">Project scope.</param>
/// <param name="DocumentId">Target document.</param>
public sealed record EnsureDocumentEmbeddingsCommand(
    Guid OrganizationId,
    Guid ProjectId,
    Guid DocumentId);
