namespace OpsFlow.Contracts.Analytics;

/// <summary>
/// Dashboard analytics summary for the caller's organization.
/// </summary>
public sealed record AnalyticsSummaryResponse(
    int ProjectCount,
    int DocumentCount,
    RagPipelineSummary RagPipeline);

/// <summary>
/// Aggregated RAG pipeline metrics.
/// </summary>
public sealed record RagPipelineSummary(
    int TotalRequests,
    int AnsweredCount,
    int InsufficientEvidenceCount,
    int FailedCount,
    double AverageDurationSeconds,
    double AverageRetrievalDurationSeconds,
    double? AverageGenerationDurationSeconds,
    double AverageEvidenceCount,
    IReadOnlyList<RagOutcomeBreakdown> OutcomeBreakdown,
    IReadOnlyList<RagDailyTrend> DailyTrend);

/// <summary>
/// One bucket in the outcome breakdown.
/// </summary>
public sealed record RagOutcomeBreakdown(string Outcome, int Count);

/// <summary>
/// One day's worth of RAG pipeline activity.
/// </summary>
public sealed record RagDailyTrend(DateOnly Date, int Requests, int Answered, int Failed);
