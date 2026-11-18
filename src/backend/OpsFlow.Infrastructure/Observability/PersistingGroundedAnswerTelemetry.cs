using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Observability;

/// <summary>
/// Decorator that delegates to <see cref="MeterGroundedAnswerTelemetry"/>
/// for in-memory BCL metrics AND persists each measurement to the database
/// for the analytics dashboard. Reads <c>OrganizationId</c> from
/// <see cref="ICallerContext"/> for tenant isolation.
/// </summary>
internal sealed class PersistingGroundedAnswerTelemetry(
    MeterGroundedAnswerTelemetry meters,
    OpsFlowDbContext db,
    ICallerContext callerContext,
    IClock clock,
    ILogger<PersistingGroundedAnswerTelemetry> logger) : IGroundedAnswerTelemetry
{
    public void Record(in GroundedAnswerMeasurement measurement)
    {
        meters.Record(in measurement);

        var organizationId = callerContext.OrganizationId;
        if (organizationId is null)
        {
            logger.LogDebug("No OrganizationId in caller context; skipping metric persistence");
            return;
        }

        var entry = new RagMetricEntry
        {
            OrganizationId = organizationId.Value,
            RecordedAt = clock.UtcNow,
            Outcome = measurement.Outcome.ToString(),
            RetrievalMode = measurement.RetrievalMode.ToString(),
            FailureCategory = measurement.Outcome == AnswerPipelineOutcome.Failed
                ? measurement.FailureCategory.ToString()
                : null,
            SelectedEvidenceCount = measurement.SelectedEvidenceCount,
            TotalDurationSeconds = measurement.TotalDuration.TotalSeconds,
            RetrievalDurationSeconds = measurement.RetrievalDuration.TotalSeconds,
            GenerationDurationSeconds = measurement.GenerationDuration?.TotalSeconds,
        };

        _ = db.RagMetricEntries.Add(entry);

        try
        {
            _ = db.SaveChanges();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist RAG metric entry");
        }
    }
}
