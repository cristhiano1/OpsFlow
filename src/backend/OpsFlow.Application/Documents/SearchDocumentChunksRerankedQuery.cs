namespace OpsFlow.Application.Documents;

/// <summary>
/// Input for the reranked search-document-chunks use case. Public shape mirrors
/// <see cref="SearchDocumentChunksHybridQuery"/>: <see cref="TopK"/> keeps the
/// same 1–50 semantics. The reranker candidate depth is an internal detail of
/// <see cref="SearchDocumentChunksRerankedService"/> and is intentionally not
/// exposed here.
/// </summary>
/// <param name="OrganizationId">The authenticated caller's organization.</param>
/// <param name="ProjectId">The project to search within.</param>
/// <param name="QueryText">Natural-language query text; forwarded unchanged to retrieval and the reranker.</param>
/// <param name="TopK">Maximum number of reranked hits to return (1–50).</param>
public sealed record SearchDocumentChunksRerankedQuery(
    Guid OrganizationId,
    Guid ProjectId,
    string QueryText,
    int TopK);
