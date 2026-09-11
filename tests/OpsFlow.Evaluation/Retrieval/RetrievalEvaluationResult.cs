namespace OpsFlow.Evaluation.Retrieval;

/// <summary>
/// The complete result of evaluating a dataset: identifying metadata, the
/// configured rank cut-offs, per-case results, and aggregate metrics per K.
/// </summary>
/// <param name="DatasetId">The evaluated dataset's identifier.</param>
/// <param name="DatasetVersion">The evaluated dataset's version.</param>
/// <param name="CaseCount">Number of cases evaluated.</param>
/// <param name="KValues">The rank cut-offs evaluated, in ascending order.</param>
/// <param name="Cases">Per-case results.</param>
/// <param name="Aggregates">Aggregate metrics for each K in <paramref name="KValues"/>.</param>
public sealed record RetrievalEvaluationResult(
    string DatasetId,
    string DatasetVersion,
    int CaseCount,
    IReadOnlyList<int> KValues,
    IReadOnlyList<RetrievalEvaluationCaseResult> Cases,
    IReadOnlyList<RetrievalEvaluationAggregateAtK> Aggregates);
