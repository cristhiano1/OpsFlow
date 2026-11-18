using Microsoft.EntityFrameworkCore;
using OpsFlow.Contracts.Analytics;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.Analytics;

/// <summary>
/// Builds the RAG pipeline summary for one organization without loading its metric
/// history: all-time figures are aggregated in SQL, and only rows inside the daily-trend
/// window are read, as untracked two-column projections.
/// </summary>
internal static class AnalyticsSummaryQuery
{
    public static async Task<RagPipelineSummary> GetRagPipelineSummaryAsync(
        OpsFlowDbContext db,
        Guid organizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var metrics = db.RagMetricEntries
            .AsNoTracking()
            .Where(m => m.OrganizationId == organizationId);

        // One row per distinct outcome, so the result size is independent of history.
        // Sums and non-null counts let the averages ignore nulls exactly like SQL AVG.
        var byOutcome = await metrics
            .GroupBy(m => m.Outcome)
            .Select(g => new OutcomeAggregate(
                g.Key,
                g.Count(),
                g.Sum(m => m.TotalDurationSeconds),
                g.Sum(m => m.RetrievalDurationSeconds),
                g.Count(m => m.GenerationDurationSeconds != null),
                g.Sum(m => m.GenerationDurationSeconds) ?? 0,
                g.Count(m => m.SelectedEvidenceCount != null),
                g.Sum(m => (long?)m.SelectedEvidenceCount) ?? 0))
            .ToListAsync(cancellationToken);

        var windowStart = AnalyticsDailyTrend.WindowStartUtc(now);
        var trendPoints = await metrics
            .Where(m => m.RecordedAt >= windowStart && m.RecordedAt <= now)
            .Select(m => new TrendPoint(m.RecordedAt, m.Outcome))
            .ToListAsync(cancellationToken);

        return Summarize(byOutcome, AnalyticsDailyTrend.Build(trendPoints, now));
    }

    private static RagPipelineSummary Summarize(
        List<OutcomeAggregate> byOutcome,
        IReadOnlyList<RagDailyTrend> dailyTrend)
    {
        var totalRequests = byOutcome.Sum(o => o.Count);
        var generationCount = byOutcome.Sum(o => o.GenerationCount);
        var evidenceCount = byOutcome.Sum(o => o.EvidenceCount);

        double? averageGeneration = generationCount > 0
            ? Math.Round(byOutcome.Sum(o => o.GenerationSum) / generationCount, 3)
            : null;

        return new RagPipelineSummary(
            totalRequests,
            CountOf(byOutcome, "Answered"),
            CountOf(byOutcome, "InsufficientEvidence"),
            CountOf(byOutcome, "Failed"),
            totalRequests > 0 ? Math.Round(byOutcome.Sum(o => o.TotalDurationSum) / totalRequests, 3) : 0,
            totalRequests > 0 ? Math.Round(byOutcome.Sum(o => o.RetrievalDurationSum) / totalRequests, 3) : 0,
            averageGeneration,
            evidenceCount > 0 ? Math.Round((double)byOutcome.Sum(o => o.EvidenceSum) / evidenceCount, 1) : 0,
            [
                .. byOutcome
                    .OrderByDescending(o => o.Count)
                    .ThenBy(o => o.Outcome, StringComparer.Ordinal)
                    .Select(o => new RagOutcomeBreakdown(o.Outcome, o.Count)),
            ],
            dailyTrend);
    }

    private static int CountOf(List<OutcomeAggregate> byOutcome, string outcome) =>
        byOutcome.Find(o => o.Outcome == outcome)?.Count ?? 0;

    internal sealed record OutcomeAggregate(
        string Outcome,
        int Count,
        double TotalDurationSum,
        double RetrievalDurationSum,
        int GenerationCount,
        double GenerationSum,
        int EvidenceCount,
        long EvidenceSum);
}
