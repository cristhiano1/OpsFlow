namespace OpsFlow.Evaluation.Retrieval;

/// <summary>
/// The full evaluation outcome for a single case: the query, the ranked
/// symbolic chunk keys that retrieval returned, the gold relevance labels, and
/// the metrics computed at each configured K.
/// </summary>
/// <param name="CaseId">The case identifier.</param>
/// <param name="Query">The query that was issued.</param>
/// <param name="RetrievedChunkKeys">Symbolic chunk keys in rank order (rank 1 first).</param>
/// <param name="GoldRelevance">The case's gold relevance labels (chunk key to grade).</param>
/// <param name="PerK">Metrics computed at each configured rank cut-off.</param>
public sealed record RetrievalEvaluationCaseResult(
    string CaseId,
    string Query,
    IReadOnlyList<string> RetrievedChunkKeys,
    IReadOnlyDictionary<string, int> GoldRelevance,
    IReadOnlyList<RetrievalEvaluationAtKResult> PerK);
