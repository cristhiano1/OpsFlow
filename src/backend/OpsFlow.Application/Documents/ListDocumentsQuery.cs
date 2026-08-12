namespace OpsFlow.Application.Documents;

/// <summary>Input for the list-documents use case.</summary>
/// <param name="OrganizationId">The authenticated caller's organization.</param>
/// <param name="ProjectId">The project whose documents are requested.</param>
public sealed record ListDocumentsQuery(Guid OrganizationId, Guid ProjectId);
