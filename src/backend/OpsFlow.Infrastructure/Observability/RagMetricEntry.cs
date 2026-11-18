namespace OpsFlow.Infrastructure.Observability;

/// <summary>
/// Persisted record of a single grounded-answer pipeline execution. Used by the
/// analytics dashboard to show RAG pipeline health over time. Carries only
/// bounded values — no user content or identifiers beyond OrganizationId for
/// tenant isolation (consistent with ADR-012).
/// </summary>
public class RagMetricEntry
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string RetrievalMode { get; set; } = string.Empty;
    public string? FailureCategory { get; set; }
    public int? SelectedEvidenceCount { get; set; }
    public double TotalDurationSeconds { get; set; }
    public double RetrievalDurationSeconds { get; set; }
    public double? GenerationDurationSeconds { get; set; }
}
