namespace OpsFlow.Contracts.Documents;

/// <summary>
/// A safe projection of document metadata. Does not expose
/// <c>OrganizationId</c> or <c>ProjectId</c> — the caller already knows their
/// own tenant and the project is encoded in the request URL.
/// </summary>
/// <param name="Id">The document's unique identifier.</param>
/// <param name="OriginalFileName">The file name as supplied by the uploader.</param>
/// <param name="ContentType">The MIME content type.</param>
/// <param name="SizeBytes">The file size in bytes.</param>
/// <param name="CreatedAt">The UTC timestamp when the document was created.</param>
public sealed record DocumentResponse(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset CreatedAt);
