using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Application.Authorization;
using OpsFlow.Application.Documents;
using OpsFlow.Contracts.Documents;

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
