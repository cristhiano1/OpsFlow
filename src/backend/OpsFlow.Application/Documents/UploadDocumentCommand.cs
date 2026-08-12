namespace OpsFlow.Application.Documents;

/// <summary>
/// Input for the upload-document use case. <see cref="ReportedContentType"/>
/// is untrusted client input — the service derives the canonical MIME type
/// from the validated file extension.
/// </summary>
public sealed record UploadDocumentCommand(
    Guid OrganizationId,
    Guid ProjectId,
    string OriginalFileName,
    string? ReportedContentType,
    long SizeBytes,
    Stream Content);
