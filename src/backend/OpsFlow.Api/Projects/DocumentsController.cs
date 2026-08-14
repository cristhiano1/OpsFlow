using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Application.Authorization;
using OpsFlow.Application.Documents;
using OpsFlow.Contracts.Documents;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Api.Projects;

/// <summary>Handles document metadata endpoints nested under a project.</summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/documents")]
[Authorize]
public sealed class DocumentsController : ControllerBase
{
    /// <summary>
    /// Lists all document metadata records for the specified project within the
    /// authenticated caller's organization.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListAsync(
        Guid projectId,
        [FromServices] ListDocumentsService listDocumentsService,
        CancellationToken cancellationToken)
    {
        if (!TryGetOrganizationId(out var organizationId))
        {
            return UnauthorizedWithoutBody();
        }

        var result = await listDocumentsService.ListAsync(
            new ListDocumentsQuery(organizationId, projectId),
            cancellationToken);

        if (!result.ProjectFound)
        {
            return NotFound();
        }

        var items = result.Documents.Select(d => new DocumentResponse(
            d.Id,
            d.OriginalFileName,
            d.ContentType,
            d.SizeBytes,
            d.CreatedAt)).ToList();

        return Ok(new DocumentListResponse(items));
    }

    /// <summary>
    /// Uploads a document to the specified project within the authenticated
    /// caller's organization.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(26 * 1024 * 1024)]
    public async Task<IActionResult> UploadAsync(
        Guid projectId,
        [FromForm] IFormFile? file,
        [FromServices] UploadDocumentService uploadDocumentService,
        CancellationToken cancellationToken)
    {
        if (!TryGetOrganizationId(out var organizationId))
        {
            return UnauthorizedWithoutBody();
        }

        if (file is null || file.Length <= 0)
        {
            return ValidationProblem("A non-empty file is required.");
        }

        using var stream = file.OpenReadStream();

        // Pass the raw filename from the multipart field; Application layer is the
        // single authority for sanitization (backslash normalization, length check).
        var command = new UploadDocumentCommand(
            organizationId,
            projectId,
            file.FileName,
            file.ContentType,
            file.Length,
            stream);

        var result = await uploadDocumentService.UploadAsync(command, cancellationToken);

        if (!result.ProjectFound)
        {
            return NotFound();
        }

        if (!result.Succeeded)
        {
            if (result.Error is not null && result.Error.Contains("MiB", StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            if (result.Error is not null && (
                result.Error.Contains("Unsupported", StringComparison.OrdinalIgnoreCase) ||
                result.Error.Contains("incompatible", StringComparison.OrdinalIgnoreCase)))
            {
                return UnprocessableEntity(result.Error);
            }

            return ValidationProblem(result.Error ?? "Upload validation failed.");
        }

        var doc = result.Document!;
        var response = new DocumentResponse(
            doc.Id,
            doc.OriginalFileName,
            doc.ContentType,
            doc.SizeBytes,
            doc.CreatedAt);

        return StatusCode(StatusCodes.Status201Created, response);
    }

    /// <summary>
    /// Streams the raw content of the specified document within the
    /// authenticated caller's organization.
    /// </summary>
    [HttpGet("{documentId:guid}/content")]
    public async Task<IActionResult> GetContentAsync(
        Guid projectId,
        Guid documentId,
        [FromServices] GetDocumentContentService getDocumentContentService,
        [FromServices] ILogger<DocumentsController> logger,
        CancellationToken cancellationToken)
    {
        if (!TryGetOrganizationId(out var organizationId))
        {
            return UnauthorizedWithoutBody();
        }

        GetDocumentContentResult result;
        try
        {
            result = await getDocumentContentService.GetAsync(
                new GetDocumentContentQuery(organizationId, projectId, documentId),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unexpected storage exception for Document {DocumentId} in Project {ProjectId}, Organization {OrganizationId}",
                documentId,
                projectId,
                organizationId);
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            return new EmptyResult();
        }

        switch (result.Status)
        {
            case GetDocumentContentStatus.NotFound:
                return NotFound();

            case GetDocumentContentStatus.StorageMissing:
                logger.LogWarning(
                    "Storage object missing for Document {DocumentId} in Project {ProjectId}, Organization {OrganizationId}",
                    documentId,
                    projectId,
                    organizationId);
                Response.StatusCode = StatusCodes.Status500InternalServerError;
                return new EmptyResult();

            case GetDocumentContentStatus.Success:
                return File(result.Content!, result.Metadata!.ContentType, result.Metadata.OriginalFileName);

            default:
                Response.StatusCode = StatusCodes.Status500InternalServerError;
                return new EmptyResult();
        }
    }

    /// <summary>
    /// Ensures a text extraction exists for the specified document. Returns the
    /// cached result if one already exists; otherwise extracts synchronously and
    /// persists the result.
    /// </summary>
    [HttpPost("{documentId:guid}/extraction")]
    public async Task<IActionResult> ExtractTextAsync(
        Guid projectId,
        Guid documentId,
        [FromServices] ExtractDocumentTextService extractService,
        [FromServices] ILogger<DocumentsController> logger,
        CancellationToken cancellationToken)
    {
        if (!TryGetOrganizationId(out var organizationId))
        {
            return UnauthorizedWithoutBody();
        }

        ExtractDocumentTextResult result;
        try
        {
            result = await extractService.ExtractAsync(
                new ExtractDocumentTextCommand(organizationId, projectId, documentId),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unexpected extraction exception for Document {DocumentId} in Project {ProjectId}, Organization {OrganizationId}",
                documentId,
                projectId,
                organizationId);
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            return new EmptyResult();
        }

        switch (result.Status)
        {
            case ExtractDocumentTextStatus.NotFound:
                return NotFound();

            case ExtractDocumentTextStatus.StorageMissing:
                logger.LogWarning(
                    "Storage object missing for Document {DocumentId} in Project {ProjectId}, Organization {OrganizationId}",
                    documentId,
                    projectId,
                    organizationId);
                Response.StatusCode = StatusCodes.Status500InternalServerError;
                return new EmptyResult();

            case ExtractDocumentTextStatus.UnsupportedFormat:
                return StatusCode(StatusCodes.Status415UnsupportedMediaType);

            case ExtractDocumentTextStatus.MalformedDocument:
                return UnprocessableEntity();

            case ExtractDocumentTextStatus.ExtractionLimitExceeded:
                return UnprocessableEntity();

            case ExtractDocumentTextStatus.SuccessCreated:
                return CreatedAtAction(
                    "GetExtraction",
                    new { projectId, documentId },
                    MapExtractionResponse(result.Extraction!));

            case ExtractDocumentTextStatus.SuccessExisting:
                return Ok(MapExtractionResponse(result.Extraction!));

            default:
                Response.StatusCode = StatusCodes.Status500InternalServerError;
                return new EmptyResult();
        }
    }

    /// <summary>
    /// Returns a previously persisted text extraction for the specified
    /// document. Never triggers extraction or modifies the database.
    /// </summary>
    [HttpGet("{documentId:guid}/extraction")]
    public async Task<IActionResult> GetExtractionAsync(
        Guid projectId,
        Guid documentId,
        [FromServices] GetDocumentExtractionService getExtractionService,
        CancellationToken cancellationToken)
    {
        if (!TryGetOrganizationId(out var organizationId))
        {
            return UnauthorizedWithoutBody();
        }

        var result = await getExtractionService.GetAsync(
            new GetDocumentExtractionQuery(organizationId, projectId, documentId),
            cancellationToken);

        if (!result.Found)
        {
            return NotFound();
        }

        return Ok(MapExtractionResponse(result.Extraction!));
    }

    private static DocumentExtractionResponse MapExtractionResponse(DocumentExtraction extraction) =>
        new(extraction.DocumentId,
            extraction.ExtractedAt,
            extraction.ExtractedText.Length,
            extraction.ExtractedText);

    private bool TryGetOrganizationId(out Guid organizationId)
    {
        organizationId = Guid.Empty;
        var claim = User.FindFirst(OpsFlowClaimTypes.OrganizationId)?.Value;
        if (string.IsNullOrWhiteSpace(claim))
        {
            return false;
        }

        if (!Guid.TryParse(claim, out organizationId) || organizationId == Guid.Empty)
        {
            return false;
        }

        return true;
    }

    private EmptyResult UnauthorizedWithoutBody()
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return new EmptyResult();
    }
}
