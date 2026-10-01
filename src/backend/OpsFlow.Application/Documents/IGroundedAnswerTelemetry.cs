namespace OpsFlow.Application.Documents;

/// <summary>
/// Provider-neutral observability sink for the grounded-answer use case. The
/// Application layer depends only on this port; concrete metric/trace exporters
/// live in outer layers. Implementations must be safe to call on the request path
/// and must never throw back into it — but callers also guard defensively, so a
/// misbehaving implementation can never affect answering (see ADR-012).
/// </summary>
public interface IGroundedAnswerTelemetry
{
    /// <summary>
    /// Records exactly one bounded measurement for a completed answer request
    /// (including failed and cancelled requests). The measurement carries no user
    /// or document content.
    /// </summary>
    void Record(in GroundedAnswerMeasurement measurement);
}
