namespace OpsFlow.Application.Documents;

/// <summary>Input for the search-document-chunks use case.</summary>
/// <param name="OrganizationId">The authenticated caller's organization.</param>
/// <param name="ProjectId">The project to search within.</param>
/// <param name="QueryText">Natural-language query text to embed and search.</param>
/// <param name="TopK">Maximum number of hits to return (1–50).</param>
public sealed record SearchDocumentChunksQuery(
    Guid OrganizationId,
    Guid ProjectId,
    string QueryText,
    int TopK);
