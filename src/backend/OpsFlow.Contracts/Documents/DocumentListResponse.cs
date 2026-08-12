namespace OpsFlow.Contracts.Documents;

/// <summary>
/// Stable API envelope for a collection of document metadata records.
/// </summary>
/// <param name="Items">The documents belonging to the requested project.</param>
public sealed record DocumentListResponse(IReadOnlyList<DocumentResponse> Items);
