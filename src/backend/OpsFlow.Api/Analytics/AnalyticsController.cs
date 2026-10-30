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

        var metrics = await db.RagMetricEntries
            .Where(m => m.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);

        var totalRequests = metrics.Count;
        var answeredCount = metrics.Count(m => m.Outcome == "Answered");
        var insufficientCount = metrics.Count(m => m.Outcome == "InsufficientEvidence");
        var failedCount = metrics.Count(m => m.Outcome == "Failed");

        var avgDuration = totalRequests > 0
            ? metrics.Average(m => m.TotalDurationSeconds) : 0;
        var avgRetrievalDuration = totalRequests > 0
            ? metrics.Average(m => m.RetrievalDurationSeconds) : 0;
        var genMetrics = metrics.Where(m => m.GenerationDurationSeconds.HasValue).ToList();
        double? avgGenerationDuration = genMetrics.Count > 0
            ? genMetrics.Average(m => m.GenerationDurationSeconds!.Value) : null;
        var evidenceMetrics = metrics.Where(m => m.SelectedEvidenceCount.HasValue).ToList();
        var avgEvidence = evidenceMetrics.Count > 0
            ? evidenceMetrics.Average(m => m.SelectedEvidenceCount!.Value) : 0;

        var outcomeBreakdown = metrics
            .GroupBy(m => m.Outcome)
            .Select(g => new RagOutcomeBreakdown(g.Key, g.Count()))
            .OrderByDescending(b => b.Count)
            .ToList();

        var dailyTrend = AnalyticsDailyTrend.Build(metrics, clock.UtcNow);

        var ragSummary = new RagPipelineSummary(
            totalRequests,
            answeredCount,
            insufficientCount,
            failedCount,
            Math.Round(avgDuration, 3),
            Math.Round(avgRetrievalDuration, 3),
            avgGenerationDuration.HasValue ? Math.Round(avgGenerationDuration.Value, 3) : null,
            Math.Round(avgEvidence, 1),
            outcomeBreakdown,
            dailyTrend);

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
