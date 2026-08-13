using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Projects;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Coordinates the upload-document use case: validates the upload, verifies
/// tenant-scoped project ownership, stores bytes, persists metadata, and
/// compensates on failure.
/// </summary>
public sealed class UploadDocumentService
{
    /// <summary>Exact maximum file size: 25 MiB.</summary>
    public const long MaxFileSizeBytes = 25L * 1024 * 1024;

    private static readonly Dictionary<string, string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    };

    private static readonly HashSet<string> IncompatibleMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html",
        "application/javascript",
        "application/x-msdownload",
        "application/x-executable",
    };

    private readonly IProjectRepository _projectRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentStorage _documentStorage;
    private readonly IClock _clock;

    /// <summary>Creates the service with its collaborators.</summary>
    public UploadDocumentService(
        IProjectRepository projectRepository,
        IDocumentRepository documentRepository,
        IDocumentStorage documentStorage,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(projectRepository);
        ArgumentNullException.ThrowIfNull(documentRepository);
        ArgumentNullException.ThrowIfNull(documentStorage);
        ArgumentNullException.ThrowIfNull(clock);

        _projectRepository = projectRepository;
        _documentRepository = documentRepository;
        _documentStorage = documentStorage;
        _clock = clock;
    }

    /// <summary>
    /// Validates, stores, and persists a document upload. Returns a result
    /// indicating success, project-not-found, or validation error.
    /// </summary>
    public async Task<UploadDocumentResult> UploadAsync(
        UploadDocumentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization ID must not be empty.", nameof(command));
        }

        // --- Upload policy validation ---

        if (command.ProjectId == Guid.Empty)
        {
            return UploadDocumentResult.ProjectNotFound();
        }

        var basename = ExtractSafeBasename(command.OriginalFileName);
        if (basename is null)
        {
            return UploadDocumentResult.ValidationError("A usable file name is required.");
        }

        if (basename.Length > Document.OriginalFileNameMaxLength)
        {
            return UploadDocumentResult.ValidationError(
                $"File name must not exceed {Document.OriginalFileNameMaxLength} characters.");
        }

        var extension = Path.GetExtension(basename);
        if (!AllowedExtensions.TryGetValue(extension, out var canonicalMime))
        {
            return UploadDocumentResult.ValidationError(
                $"Unsupported file type '{extension}'. Allowed: .pdf, .txt, .docx");
        }

        if (command.SizeBytes <= 0)
        {
            return UploadDocumentResult.ValidationError("File must not be empty.");
        }

        if (command.SizeBytes > MaxFileSizeBytes)
        {
            return UploadDocumentResult.ValidationError(
                $"File size exceeds the {MaxFileSizeBytes / (1024 * 1024)} MiB limit.");
        }

        if (!IsReportedMimeCompatible(command.ReportedContentType, canonicalMime))
        {
            return UploadDocumentResult.ValidationError(
                $"Reported content type '{command.ReportedContentType}' is incompatible with extension '{extension}'.");
        }

        // --- Project tenancy check ---

        var exists = await _projectRepository.ExistsInOrganizationAsync(
            command.ProjectId, command.OrganizationId, cancellationToken);

        if (!exists)
        {
            return UploadDocumentResult.ProjectNotFound();
        }

        // --- Storage + metadata persistence ---

        var documentId = Guid.NewGuid();
        var address = new DocumentStorageAddress(command.OrganizationId, command.ProjectId, documentId);

        await _documentStorage.SaveAsync(address, command.Content, cancellationToken);

        // Once storage completes, use CancellationToken.None for consistency-critical operations.
        // A client disconnect must not interrupt metadata persistence or compensating deletion.
        var document = new Document(
            documentId,
            command.OrganizationId,
            command.ProjectId,
            basename,
            canonicalMime,
            command.SizeBytes,
            _clock.UtcNow);

        try
        {
            await _documentRepository.AddAsync(document, CancellationToken.None);
        }
        catch (Exception)
        {
            try
            {
                await _documentStorage.DeleteAsync(address, CancellationToken.None);
            }
            catch
            {
                // Compensating delete failed — orphaned file at address.
                // The original persistence failure is the primary concern.
            }

            throw;
        }

        return UploadDocumentResult.Success(document);
    }

    private static string? ExtractSafeBasename(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        // Normalize Windows-style separators before extracting the basename so
        // that "C:\Users\attacker\file.pdf" → "file.pdf" on any host OS.
        var basename = Path.GetFileName(fileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(basename))
        {
            return null;
        }

        return basename;
    }

    private static bool IsReportedMimeCompatible(string? reportedMime, string canonicalMime)
    {
        if (string.IsNullOrWhiteSpace(reportedMime))
        {
            return true;
        }

        if (string.Equals(reportedMime, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(reportedMime, canonicalMime, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IncompatibleMimeTypes.Contains(reportedMime))
        {
            return false;
        }

        return false;
    }
}
