using OpsFlow.Api.Analytics;
using OpsFlow.Infrastructure.Observability;

namespace OpsFlow.Api.IntegrationTests.Analytics;

// Pure calculation tests: no SQL Server fixture required.
public sealed class AnalyticsDailyTrendTests
{
    // 14:30 UTC on 2026-10-30 → window is 2026-10-01 .. 2026-10-30 inclusive.
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly ExpectedWindowStart = new(2026, 10, 1);

    private static RagMetricEntry Metric(DateTimeOffset recordedAt, string outcome = "Answered") =>
        new() { RecordedAt = recordedAt, Outcome = outcome, RetrievalMode = "Hybrid" };

    [Fact]
    public void Window_start_is_today_minus_29_utc_days()
    {
        Assert.Equal(ExpectedWindowStart, AnalyticsDailyTrend.WindowStart(Now));
    }

    [Fact]
    public void Metric_older_than_30_days_is_excluded()
    {
        var trend = AnalyticsDailyTrend.Build([Metric(Now.AddDays(-31))], Now);

        Assert.Empty(trend);
    }

    [Fact]
    public void Metric_inside_window_is_included()
    {
        var trend = AnalyticsDailyTrend.Build([Metric(Now.AddDays(-10))], Now);

        var day = Assert.Single(trend);
        Assert.Equal(new DateOnly(2026, 10, 20), day.Date);
        Assert.Equal(1, day.Requests);
    }

    [Fact]
    public void Today_is_included()
    {
        var trend = AnalyticsDailyTrend.Build([Metric(Now)], Now);

        Assert.Equal(new DateOnly(2026, 10, 30), Assert.Single(trend).Date);
    }

    [Fact]
    public void Boundary_is_utc_midnight_of_the_first_window_day()
    {
        var windowStartUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        var trend = AnalyticsDailyTrend.Build(
            [Metric(windowStartUtc), Metric(windowStartUtc.AddTicks(-1))],
            Now);

        var day = Assert.Single(trend);
        Assert.Equal(ExpectedWindowStart, day.Date);
        Assert.Equal(1, day.Requests);
    }

    [Fact]
    public void Non_utc_offsets_are_bucketed_by_utc_date()
    {
        // 2026-09-30 23:30 at -02:00 is 2026-10-01 01:30 UTC → inside the window.
        var inside = new DateTimeOffset(2026, 9, 30, 23, 30, 0, TimeSpan.FromHours(-2));
        // 2026-10-01 01:30 at +03:00 is 2026-09-30 22:30 UTC → outside the window.
        var outside = new DateTimeOffset(2026, 10, 1, 1, 30, 0, TimeSpan.FromHours(3));

        var trend = AnalyticsDailyTrend.Build([Metric(inside), Metric(outside)], Now);

        var day = Assert.Single(trend);
        Assert.Equal(ExpectedWindowStart, day.Date);
    }

    [Fact]
    public void Sparse_old_activity_does_not_reappear_in_the_chart()
    {
        // Under the old "last 30 active days" logic every one of these would be charted.
        var oldActivity = Enumerable.Range(0, 40)
            .Select(i => Metric(Now.AddDays(-60 - (i * 7))))
            .ToList();

        var trend = AnalyticsDailyTrend.Build([.. oldActivity, Metric(Now.AddDays(-2))], Now);

        var day = Assert.Single(trend);
        Assert.Equal(new DateOnly(2026, 10, 28), day.Date);
        Assert.All(trend, d => Assert.True(d.Date >= ExpectedWindowStart));
    }

    [Fact]
    public void Days_are_ordered_and_outcomes_counted_per_day()
    {
        var trend = AnalyticsDailyTrend.Build(
            [
                Metric(Now.AddDays(-1), "Failed"),
                Metric(Now.AddDays(-5), "Answered"),
                Metric(Now.AddDays(-1), "Answered"),
                Metric(Now.AddDays(-1), "InsufficientEvidence"),
            ],
            Now);

        Assert.Collection(
            trend,
            d =>
            {
                Assert.Equal(new DateOnly(2026, 10, 25), d.Date);
                Assert.Equal((1, 1, 0), (d.Requests, d.Answered, d.Failed));
            },
            d =>
            {
                Assert.Equal(new DateOnly(2026, 10, 29), d.Date);
                Assert.Equal((3, 1, 1), (d.Requests, d.Answered, d.Failed));
            });
    }
}
