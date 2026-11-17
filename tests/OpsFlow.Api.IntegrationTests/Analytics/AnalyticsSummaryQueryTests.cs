using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpsFlow.Api.Analytics;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Contracts.Analytics;
using OpsFlow.Infrastructure.Observability;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Analytics;

// Runs the real query against SQL Server, which also proves EF translates the
// aggregates server-side. Every test uses fresh organization IDs.
[Collection(SqlServerCollection.Name)]
public sealed class AnalyticsSummaryQueryTests
{
    // Window: 2026-10-01T00:00:00Z .. 2026-10-30T14:30:00Z.
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowStartUtc = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public AnalyticsSummaryQueryTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Empty_organization_returns_zero_and_null_aggregates()
    {
        var summary = await SummarizeAsync(Guid.NewGuid());

        Assert.Equal(0, summary.TotalRequests);
        Assert.Equal(0, summary.AnsweredCount);
        Assert.Equal(0, summary.InsufficientEvidenceCount);
        Assert.Equal(0, summary.FailedCount);
        Assert.Equal(0.0, summary.AverageDurationSeconds);
        Assert.Equal(0.0, summary.AverageRetrievalDurationSeconds);
        Assert.Null(summary.AverageGenerationDurationSeconds);
        Assert.Equal(0.0, summary.AverageEvidenceCount);
        Assert.Empty(summary.OutcomeBreakdown);
        Assert.Empty(summary.DailyTrend);
    }

    [Fact]
    public async Task Counts_averages_and_breakdown_are_correct()
    {
        var org = Guid.NewGuid();
        var at = Now.AddDays(-1);
        await InsertAsync(
            Metric(org, at, "Answered", total: 1.0, retrieval: 0.2, generation: 0.5, evidence: 2),
            Metric(org, at, "Answered", total: 2.0, retrieval: 0.4, generation: 1.5, evidence: 4),
            Metric(org, at, "InsufficientEvidence", total: 3.0, retrieval: 0.6, generation: null, evidence: 6),
            Metric(org, at, "Failed", total: 6.0, retrieval: 0.8, generation: null, evidence: null));

        var summary = await SummarizeAsync(org);

        Assert.Equal(4, summary.TotalRequests);
        Assert.Equal(2, summary.AnsweredCount);
        Assert.Equal(1, summary.InsufficientEvidenceCount);
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(3.0, summary.AverageDurationSeconds, 3);
        Assert.Equal(0.5, summary.AverageRetrievalDurationSeconds, 3);
        Assert.NotNull(summary.AverageGenerationDurationSeconds);
        Assert.Equal(1.0, summary.AverageGenerationDurationSeconds.Value, 3);
        Assert.Equal(4.0, summary.AverageEvidenceCount, 1);

        RagOutcomeBreakdown[] expectedBreakdown =
        [
            new("Answered", 2),
            new("Failed", 1),
            new("InsufficientEvidence", 1),
        ];
        Assert.Equal(expectedBreakdown, summary.OutcomeBreakdown);
    }

    [Fact]
    public async Task Totals_are_all_time_while_the_trend_covers_only_the_window_up_to_now()
    {
        var org = Guid.NewGuid();
        await InsertAsync(
            Metric(org, Now.AddDays(-90)),
            Metric(org, WindowStartUtc.AddTicks(-1)),
            Metric(org, WindowStartUtc),
            Metric(org, Now.AddDays(-1), "Failed"),
            Metric(org, Now),
            Metric(org, Now.AddHours(1)),
            Metric(org, Now.AddDays(3)));

        var summary = await SummarizeAsync(org);

        Assert.Equal(7, summary.TotalRequests);

        RagDailyTrend[] expectedTrend =
        [
            new(new DateOnly(2026, 10, 1), 1, 1, 0),
            new(new DateOnly(2026, 10, 29), 1, 0, 1),
            new(new DateOnly(2026, 10, 30), 1, 1, 0),
        ];
        Assert.Equal(expectedTrend, summary.DailyTrend);
    }

    [Fact]
    public async Task One_tenant_never_affects_another_tenants_summary()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        await InsertAsync(
            Metric(orgA, Now.AddDays(-2), "Failed", total: 9.0),
            Metric(orgA, Now.AddDays(-2), "Failed", total: 9.0),
            Metric(orgA, Now.AddDays(-40), "InsufficientEvidence"),
            Metric(orgB, Now.AddDays(-1), "Answered", total: 1.0));

        var summaryA = await SummarizeAsync(orgA);
        var summaryB = await SummarizeAsync(orgB);

        Assert.Equal(3, summaryA.TotalRequests);
        Assert.Equal(1, summaryB.TotalRequests);
        Assert.Equal(1.0, summaryB.AverageDurationSeconds, 3);
        Assert.Equal(new RagOutcomeBreakdown("Answered", 1), Assert.Single(summaryB.OutcomeBreakdown));
        Assert.Equal(new RagDailyTrend(new DateOnly(2026, 10, 29), 1, 1, 0), Assert.Single(summaryB.DailyTrend));
        Assert.Equal(2, summaryA.FailedCount);
        Assert.Equal(6.333, summaryA.AverageDurationSeconds, 3);
    }

    [Fact]
    public async Task Summary_does_not_materialize_metric_entities()
    {
        var org = Guid.NewGuid();
        var history = Enumerable.Range(0, 250)
            .Select(i => Metric(org, Now.AddDays(-100 - (i * 3))))
            .ToArray();
        await InsertAsync(history);
        await InsertAsync(Metric(org, Now.AddDays(-1)), Metric(org, Now.AddDays(-2)), Metric(org, Now.AddDays(-3)));

        var counter = new MaterializationCounter();
        var summary = await SummarizeAsync(org, counter);

        Assert.Equal(253, summary.TotalRequests);
        Assert.Equal(3, summary.DailyTrend.Sum(d => d.Requests));
        Assert.Equal(0, counter.RagMetricEntries);

        // Positive control: the interceptor does observe entity materialization.
        await using (var db = CreateContext(counter))
        {
            _ = await db.RagMetricEntries.FirstAsync(m => m.OrganizationId == org);
        }

        Assert.Equal(1, counter.RagMetricEntries);
    }

    private async Task<RagPipelineSummary> SummarizeAsync(Guid organizationId, MaterializationCounter? counter = null)
    {
        await using var db = CreateContext(counter);
        return await AnalyticsSummaryQuery.GetRagPipelineSummaryAsync(db, organizationId, Now, CancellationToken.None);
    }

    private async Task InsertAsync(params RagMetricEntry[] entries)
    {
        await using var db = _fixture.CreateContext();
        db.RagMetricEntries.AddRange(entries);
        await db.SaveChangesAsync();
    }

    private OpsFlowDbContext CreateContext(MaterializationCounter? counter)
    {
        var builder = new DbContextOptionsBuilder<OpsFlowDbContext>().UseSqlServer(_fixture.ConnectionString);
        if (counter is not null)
        {
            builder.AddInterceptors(counter);
        }

        return new OpsFlowDbContext(builder.Options);
    }

    private static RagMetricEntry Metric(
        Guid organizationId,
        DateTimeOffset recordedAt,
        string outcome = "Answered",
        double total = 1.0,
        double retrieval = 0.3,
        double? generation = 0.6,
        int? evidence = 4) =>
        new()
        {
            OrganizationId = organizationId,
            RecordedAt = recordedAt,
            Outcome = outcome,
            RetrievalMode = "Hybrid",
            TotalDurationSeconds = total,
            RetrievalDurationSeconds = retrieval,
            GenerationDurationSeconds = generation,
            SelectedEvidenceCount = evidence,
        };

    private sealed class MaterializationCounter : IMaterializationInterceptor
    {
        public int RagMetricEntries { get; private set; }

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is RagMetricEntry)
            {
                RagMetricEntries++;
            }

            return entity;
        }
    }
}
