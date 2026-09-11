namespace OpsFlow.Evaluation.Retrieval;

/// <summary>
/// Aggregate retrieval metrics at a single rank cut-off K, as the unweighted
/// mean across all evaluated cases.
/// </summary>
/// <param name="K">The rank cut-off.</param>
/// <param name="CaseCount">Number of cases contributing to the means.</param>
/// <param name="MeanRecall">Mean Recall@K across cases.</param>
/// <param name="MeanReciprocalRank">Mean reciprocal rank within K across cases (MRR@K).</param>
/// <param name="MeanNdcg">Mean nDCG@K across cases.</param>
public sealed record RetrievalEvaluationAggregateAtK(
    int K,
    int CaseCount,
    double MeanRecall,
    double MeanReciprocalRank,
    double MeanNdcg);
