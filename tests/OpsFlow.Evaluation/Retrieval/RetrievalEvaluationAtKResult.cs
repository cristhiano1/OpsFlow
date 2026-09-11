namespace OpsFlow.Evaluation.Retrieval;

/// <summary>Per-case retrieval metrics computed at a single rank cut-off K.</summary>
/// <param name="K">The rank cut-off.</param>
/// <param name="Recall">Recall@K in [0, 1].</param>
/// <param name="ReciprocalRank">Reciprocal rank within K in [0, 1].</param>
/// <param name="Ndcg">nDCG@K in [0, 1].</param>
public sealed record RetrievalEvaluationAtKResult(
    int K,
    double Recall,
    double ReciprocalRank,
    double Ndcg);
