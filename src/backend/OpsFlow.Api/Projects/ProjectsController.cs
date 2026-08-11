using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Application.Authorization;
using OpsFlow.Application.Projects;
using OpsFlow.Contracts.Projects;

namespace OpsFlow.Api.Projects;

/// <summary>Handles project endpoints for the OpsFlow API.</summary>
[ApiController]
[Route("api/v1/projects")]
[Authorize]
public sealed class ProjectsController : ControllerBase
{
    /// <summary>Creates a new project in the authenticated caller's organization.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateProjectRequest? request,
        [FromServices] CreateProjectService createProjectService,
        CancellationToken cancellationToken)
    {
        if (!TryGetOrganizationId(out var organizationId))
        {
            return UnauthorizedWithoutBody();
        }

        if (request is null)
        {
            return ValidationProblem();
        }

        var result = await createProjectService.CreateAsync(
            new CreateProjectCommand(organizationId, request.Name, request.Description),
            cancellationToken);

        if (!result.Succeeded || result.Project is null)
        {
            return ValidationProblem(detail: result.Error);
        }

        var project = result.Project;
        var response = new ProjectResponse(
            project.Id,
            project.Name,
            project.Description,
            project.CreatedAt);

        return StatusCode(StatusCodes.Status201Created, response);
    }

    /// <summary>Lists all projects belonging to the authenticated caller's organization.</summary>
    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromServices] ListProjectsService listProjectsService,
        CancellationToken cancellationToken)
    {
        if (!TryGetOrganizationId(out var organizationId))
        {
            return UnauthorizedWithoutBody();
        }

        var projects = await listProjectsService.ListAsync(
            new ListProjectsQuery(organizationId),
            cancellationToken);

        var items = projects.Select(p => new ProjectResponse(
            p.Id,
            p.Name,
            p.Description,
            p.CreatedAt)).ToList();

        return Ok(new ProjectListResponse(items));
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
