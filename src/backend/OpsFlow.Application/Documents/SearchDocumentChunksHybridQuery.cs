namespace OpsFlow.Application.Documents;

/// <summary>Input for the hybrid search-document-chunks use case.</summary>
/// <param name="OrganizationId">The authenticated caller's organization.</param>
/// <param name="ProjectId">The project to search within.</param>
/// <param name="QueryText">Natural-language query text for hybrid retrieval.</param>
/// <param name="TopK">Maximum number of fused hits to return (1–50).</param>
public sealed record SearchDocumentChunksHybridQuery(
    Guid OrganizationId,
    Guid ProjectId,
    string QueryText,
    int TopK);
