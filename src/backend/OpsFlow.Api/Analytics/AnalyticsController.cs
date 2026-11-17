using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Api.RateLimiting;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Authorization;
using OpsFlow.Contracts.Analytics;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.Analytics;

/// <summary>Serves analytics data for the organization dashboard.</summary>
[ApiController]
[Route("api/v1/analytics")]
[Authorize(Policy = AuthorizationPolicies.AnalyticsRead)]
[EnableRateLimiting(RateLimitPolicies.ApiStandard)]
public sealed class AnalyticsController : ControllerBase
{
    /// <summary>Returns an aggregated analytics summary for the caller's organization.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummaryAsync(
        [FromServices] OpsFlowDbContext db,
        [FromServices] IClock clock,
        CancellationToken cancellationToken)
    {
        if (!TryGetOrganizationId(out var organizationId))
        {
            return UnauthorizedWithoutBody();
        }

        var projectCount = await db.Projects
            .CountAsync(p => p.OrganizationId == organizationId, cancellationToken);

        var documentCount = await db.Documents
            .CountAsync(d => d.OrganizationId == organizationId, cancellationToken);

        var ragSummary = await AnalyticsSummaryQuery.GetRagPipelineSummaryAsync(
            db, organizationId, clock.UtcNow, cancellationToken);

        return Ok(new AnalyticsSummaryResponse(projectCount, documentCount, ragSummary));
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
