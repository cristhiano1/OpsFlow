namespace OpsFlow.Evaluation.Metrics;

/// <summary>
/// Deterministic ranked-retrieval metric primitives. Each function operates on
/// numeric relevance grades only — never on chunk keys or pipeline types — so
/// the mathematics is fully isolated and unit-testable against hand-computed
/// expected values.
///
/// <para>
/// The input <c>retrievedGrades</c> is the gold relevance grade of each result
/// in rank order: <c>retrievedGrades[0]</c> is the grade of the rank-1 result,
/// and so on. A retrieved item that is not relevant has grade 0. "Relevant"
/// (for <see cref="RecallAtK"/> and <see cref="ReciprocalRankAtK"/>) means
/// grade &gt;= 1. <see cref="NdcgAtK"/> uses the graded values directly.
/// </para>
/// </summary>
public static class RetrievalMetrics
{
    /// <summary>Minimum grade that counts as relevant for binary metrics.</summary>
    public const int RelevantGradeThreshold = 1;

    /// <summary>
    /// Recall@K = (number of gold-relevant items appearing within the first K
    /// ranks) / (total number of gold-relevant items for the case).
    /// </summary>
    /// <param name="retrievedGrades">Gold grades of retrieved items in rank order.</param>
    /// <param name="totalRelevant">Total gold-relevant items for the case (must be &gt; 0).</param>
    /// <param name="k">Rank cut-off (must be &gt; 0).</param>
    public static double RecallAtK(IReadOnlyList<int> retrievedGrades, int totalRelevant, int k)
    {
        ArgumentNullException.ThrowIfNull(retrievedGrades);
        ThrowIfInvalidK(k);

        if (totalRelevant <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalRelevant),
                totalRelevant,
                "totalRelevant must be > 0; a case with no relevant items has undefined recall.");
        }

        int limit = Math.Min(k, retrievedGrades.Count);
        int relevantRetrieved = 0;
        for (int i = 0; i < limit; i++)
        {
            if (retrievedGrades[i] >= RelevantGradeThreshold)
            {
                relevantRetrieved++;
            }
        }

        return relevantRetrieved / (double)totalRelevant;
    }

    /// <summary>
    /// Reciprocal rank within K: <c>1 / rank</c> of the first relevant result at
    /// rank &lt;= K, or 0 if no relevant result appears within K. Mean Reciprocal
    /// Rank (MRR@K) is the mean of this value across cases (see the evaluator).
    /// </summary>
    /// <param name="retrievedGrades">Gold grades of retrieved items in rank order.</param>
    /// <param name="k">Rank cut-off (must be &gt; 0).</param>
    public static double ReciprocalRankAtK(IReadOnlyList<int> retrievedGrades, int k)
    {
        ArgumentNullException.ThrowIfNull(retrievedGrades);
        ThrowIfInvalidK(k);

        int limit = Math.Min(k, retrievedGrades.Count);
        for (int i = 0; i < limit; i++)
        {
            if (retrievedGrades[i] >= RelevantGradeThreshold)
            {
                return 1.0 / (i + 1);
            }
        }

        return 0.0;
    }

    /// <summary>
    /// nDCG@K with exponential gain and logarithmic discount:
    /// <c>gain(rel) = 2^rel - 1</c>, discounted by <c>log2(rank + 1)</c> over
    /// 1-based ranks. IDCG@K is the DCG of the ideal ordering (all gold grades
    /// sorted descending, truncated to K). Throws when IDCG is zero (no relevant
    /// items), which the dataset validator prevents at the case level.
    /// </summary>
    /// <param name="retrievedGrades">Gold grades of retrieved items in rank order.</param>
    /// <param name="goldRelevantGrades">All gold-relevant grades (&gt;= 1) for the case.</param>
    /// <param name="k">Rank cut-off (must be &gt; 0).</param>
    public static double NdcgAtK(
        IReadOnlyList<int> retrievedGrades,
        IReadOnlyList<int> goldRelevantGrades,
        int k)
    {
        ArgumentNullException.ThrowIfNull(retrievedGrades);
        ArgumentNullException.ThrowIfNull(goldRelevantGrades);
        ThrowIfInvalidK(k);

        double dcg = DiscountedCumulativeGain(retrievedGrades, k);

        int[] idealOrder = [.. goldRelevantGrades.OrderByDescending(grade => grade)];
        double idcg = DiscountedCumulativeGain(idealOrder, k);

        if (idcg <= 0.0)
        {
            throw new ArgumentException(
                "IDCG is zero: the case has no relevant items, so nDCG is undefined.",
                nameof(goldRelevantGrades));
        }

        return dcg / idcg;
    }

    private static double DiscountedCumulativeGain(IReadOnlyList<int> grades, int k)
    {
        int limit = Math.Min(k, grades.Count);
        double dcg = 0.0;
        for (int i = 0; i < limit; i++)
        {
            // rank = i + 1 (1-based); discount = log2(rank + 1) = log2(i + 2).
            double gain = Math.Pow(2.0, grades[i]) - 1.0;
            dcg += gain / Math.Log2(i + 2);
        }

        return dcg;
    }

    private static void ThrowIfInvalidK(int k)
    {
        if (k <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, "K must be > 0.");
        }
    }
}
