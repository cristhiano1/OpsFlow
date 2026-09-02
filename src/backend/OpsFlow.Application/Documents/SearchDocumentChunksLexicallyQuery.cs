namespace OpsFlow.Application.Documents;

/// <summary>Input for the lexical search-document-chunks use case.</summary>
/// <param name="OrganizationId">The authenticated caller's organization.</param>
/// <param name="ProjectId">The project to search within.</param>
/// <param name="QueryText">Natural-language query text for full-text search.</param>
/// <param name="TopK">Maximum number of hits to return (1–50).</param>
public sealed record SearchDocumentChunksLexicallyQuery(
    Guid OrganizationId,
    Guid ProjectId,
    string QueryText,
    int TopK);
