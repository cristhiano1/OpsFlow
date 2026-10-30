using OpsFlow.Contracts.Analytics;
using OpsFlow.Infrastructure.Observability;

namespace OpsFlow.Api.Analytics;

/// <summary>Builds the per-day RAG activity series for the last <see cref="WindowDays"/> UTC calendar days.</summary>
internal static class AnalyticsDailyTrend
{
    public const int WindowDays = 30;

    /// <summary>First UTC calendar day in the window: today and the previous 29 days.</summary>
    public static DateOnly WindowStart(DateTimeOffset now) =>
        DateOnly.FromDateTime(now.UtcDateTime).AddDays(-(WindowDays - 1));

    public static IReadOnlyList<RagDailyTrend> Build(IEnumerable<RagMetricEntry> metrics, DateTimeOffset now)
    {
        var windowStart = WindowStart(now);

        return
        [
            .. metrics
                .Select(m => (Day: DateOnly.FromDateTime(m.RecordedAt.UtcDateTime), m.Outcome))
                .Where(m => m.Day >= windowStart)
                .GroupBy(m => m.Day)
                .OrderBy(g => g.Key)
                .Select(g => new RagDailyTrend(
                    g.Key,
                    g.Count(),
                    g.Count(m => m.Outcome == "Answered"),
                    g.Count(m => m.Outcome == "Failed"))),
        ];
    }
}
