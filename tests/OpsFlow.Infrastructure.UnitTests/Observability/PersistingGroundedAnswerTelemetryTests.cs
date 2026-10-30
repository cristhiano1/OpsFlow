using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Observability;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.UnitTests.TestSupport;

namespace OpsFlow.Infrastructure.UnitTests.Observability;

public sealed class PersistingGroundedAnswerTelemetryTests : IDisposable
{
    private static readonly Guid OrgId = new("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MeterGroundedAnswerTelemetry _meters = new();
    private readonly FixedClock _clock = new(FixedNow);

    public void Dispose() => _meters.Dispose();

    private static OpsFlowDbContext CreateContext(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<OpsFlowDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options;
        return new OpsFlowDbContext(options);
    }

    private static GroundedAnswerMeasurement AnsweredMeasurement() => new(
        Outcome: AnswerPipelineOutcome.Answered,
        RetrievalMode: AnswerRetrievalMode.Hybrid,
        FailureCategory: AnswerFailureCategory.None,
        SelectedEvidenceCount: 4,
        TotalDuration: TimeSpan.FromSeconds(1.5),
        RetrievalDuration: TimeSpan.FromSeconds(0.4),
        GenerationDuration: TimeSpan.FromSeconds(0.9));

    private static GroundedAnswerMeasurement FailedMeasurement() => new(
        Outcome: AnswerPipelineOutcome.Failed,
        RetrievalMode: AnswerRetrievalMode.NotApplicable,
        FailureCategory: AnswerFailureCategory.AnswerProviderFailure,
        SelectedEvidenceCount: null,
        TotalDuration: TimeSpan.FromSeconds(0.5),
        RetrievalDuration: TimeSpan.FromSeconds(0.3),
        GenerationDuration: null);

    [Fact]
    public void Record_persists_entry_with_correct_field_values_when_organization_id_is_set()
    {
        using var db = CreateContext();
        var sut = Build(db, new FakeCallerContext(OrgId));

        var m = AnsweredMeasurement();
        sut.Record(in m);

        var entry = Assert.Single(db.RagMetricEntries);
        Assert.Equal(OrgId, entry.OrganizationId);
        Assert.Equal("Answered", entry.Outcome);
        Assert.Equal("Hybrid", entry.RetrievalMode);
        Assert.Null(entry.FailureCategory);
        Assert.Equal(4, entry.SelectedEvidenceCount);
        Assert.Equal(1.5, entry.TotalDurationSeconds, 3);
        Assert.Equal(0.4, entry.RetrievalDurationSeconds, 3);
        Assert.Equal(0.9, entry.GenerationDurationSeconds!.Value, 3);
        Assert.Equal(FixedNow, entry.RecordedAt);
    }

    [Fact]
    public void Record_sets_failure_category_and_clears_generation_duration_for_failed_outcome()
    {
        using var db = CreateContext();
        var sut = Build(db, new FakeCallerContext(OrgId));

        var m = FailedMeasurement();
        sut.Record(in m);

        var entry = Assert.Single(db.RagMetricEntries);
        Assert.Equal("Failed", entry.Outcome);
        Assert.Equal("AnswerProviderFailure", entry.FailureCategory);
        Assert.Null(entry.GenerationDurationSeconds);
        Assert.Null(entry.SelectedEvidenceCount);
    }

    [Fact]
    public void Record_skips_persistence_and_does_not_throw_when_organization_id_is_null()
    {
        using var db = CreateContext();
        var sut = Build(db, new FakeCallerContext(organizationId: null));

        var m = AnsweredMeasurement();
        sut.Record(in m);

        Assert.Empty(db.RagMetricEntries);
    }

    [Fact]
    public void Record_does_not_throw_when_save_changes_fails()
    {
        using var throwingDb = new ThrowingDbContext();
        var sut = Build(throwingDb, new FakeCallerContext(OrgId));

        var m = AnsweredMeasurement();
        // The failure must be swallowed — the answer pipeline must not be disrupted.
        var ex = Record.Exception(() => sut.Record(in m));
        Assert.Null(ex);
    }

    private PersistingGroundedAnswerTelemetry Build(OpsFlowDbContext db, ICallerContext caller)
        => new(_meters, db, caller, _clock, NullLogger<PersistingGroundedAnswerTelemetry>.Instance);

    private sealed class FakeCallerContext(Guid? organizationId) : ICallerContext
    {
        public Guid? OrganizationId { get; } = organizationId;
    }

    // Minimal subclass that forces SaveChanges to throw, simulating any transient DB error.
    private sealed class ThrowingDbContext()
        : OpsFlowDbContext(new DbContextOptionsBuilder<OpsFlowDbContext>()
            .UseInMemoryDatabase("throwing-" + Guid.NewGuid())
            .Options)
    {
        public override int SaveChanges()
            => throw new InvalidOperationException("simulated save failure");

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
            => throw new InvalidOperationException("simulated save failure");
    }
}
