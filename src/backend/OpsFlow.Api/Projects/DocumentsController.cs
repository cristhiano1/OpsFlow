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
