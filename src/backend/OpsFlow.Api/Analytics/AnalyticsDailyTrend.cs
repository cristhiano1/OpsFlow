using OpsFlow.Contracts.Analytics;

namespace OpsFlow.Api.Analytics;

/// <summary>The two columns of a RAG metric that the daily trend needs.</summary>
internal readonly record struct TrendPoint(DateTimeOffset RecordedAt, string Outcome);

/// <summary>Builds the per-day RAG activity series for the last <see cref="WindowDays"/> UTC calendar days.</summary>
internal static class AnalyticsDailyTrend
{
    public const int WindowDays = 30;

    /// <summary>First UTC calendar day in the window: today and the previous 29 days.</summary>
    public static DateOnly WindowStart(DateTimeOffset now) =>
        DateOnly.FromDateTime(now.UtcDateTime).AddDays(-(WindowDays - 1));

    /// <summary>UTC midnight of <see cref="WindowStart"/>, the inclusive lower bound of the window.</summary>
    public static DateTimeOffset WindowStartUtc(DateTimeOffset now) =>
        new(WindowStart(now).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>
    /// Groups points inside [<see cref="WindowStartUtc"/>, <paramref name="now"/>] by UTC date.
    /// Points outside the window, including future timestamps, are ignored.
    /// </summary>
    public static IReadOnlyList<RagDailyTrend> Build(IEnumerable<TrendPoint> points, DateTimeOffset now)
    {
        var windowStart = WindowStartUtc(now);

        return
        [
            .. points
                .Where(p => p.RecordedAt >= windowStart && p.RecordedAt <= now)
                .GroupBy(p => DateOnly.FromDateTime(p.RecordedAt.UtcDateTime))
                .OrderBy(g => g.Key)
                .Select(g => new RagDailyTrend(
                    g.Key,
                    g.Count(),
                    g.Count(p => p.Outcome == "Answered"),
                    g.Count(p => p.Outcome == "Failed"))),
        ];
    }
}
