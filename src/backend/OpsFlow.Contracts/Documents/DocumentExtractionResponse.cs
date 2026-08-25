namespace OpsFlow.Contracts.Documents;

/// <summary>
/// A safe projection of a document text extraction result. Does not expose
/// <c>OrganizationId</c> or <c>ProjectId</c> — the caller already knows
/// their own tenant and the project is encoded in the request URL.
/// </summary>
/// <param name="DocumentId">The document this extraction belongs to.</param>
/// <param name="ExtractedAt">The UTC timestamp when the extraction was performed.</param>
/// <param name="CharacterCount">The number of characters in the extracted text.</param>
/// <param name="Text">The normalized extracted text.</param>
public sealed record DocumentExtractionResponse(
    Guid DocumentId,
    DateTimeOffset ExtractedAt,
    int CharacterCount,
    string Text);
