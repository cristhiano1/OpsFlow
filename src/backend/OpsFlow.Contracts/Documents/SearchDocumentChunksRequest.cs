namespace OpsFlow.Contracts.Documents;

/// <summary>
/// The public request body for searching document chunks within a project.
/// <c>OrganizationId</c> is intentionally absent because the tenant identity
/// is extracted exclusively from the authenticated JWT. <c>ProjectId</c> is
/// encoded in the request URL.
/// </summary>
/// <param name="QueryText">Natural-language search query.</param>
/// <param name="TopK">Optional maximum number of results to return. Defaults to 10 when omitted.</param>
public sealed record SearchDocumentChunksRequest(string? QueryText, int? TopK);
