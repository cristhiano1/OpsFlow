using OpsFlow.Evaluation.Metrics;

namespace OpsFlow.Evaluation.UnitTests.Metrics;

public sealed class NdcgAtKTests
{
    private const double Tolerance = 1e-10;

    [Fact]
    public void Ideal_ranking_returns_one()
    {
        int[] retrieved = [3, 1];
        int[] gold = [3, 1];
        Assert.Equal(1.0, RetrievalMetrics.NdcgAtK(retrieved, gold, k: 5), Tolerance);
    }

    [Fact]
    public void Degraded_order_scores_below_one_with_expected_value()
    {
        int[] retrieved = [1, 3];
        int[] gold = [3, 1];

        // Manual reference computation from gain = 2^rel - 1, discount = log2(rank + 1).
        double dcg = (1.0 / Math.Log2(2)) + (7.0 / Math.Log2(3));
        double idcg = (7.0 / Math.Log2(2)) + (1.0 / Math.Log2(3));
        double expected = dcg / idcg;

        double actual = RetrievalMetrics.NdcgAtK(retrieved, gold, k: 5);
        Assert.Equal(expected, actual, Tolerance);
        Assert.True(actual < 1.0);
    }

    [Fact]
    public void Higher_grade_ranked_first_outscores_lower_grade_ranked_first()
    {
        int[] gold = [3, 1];
        int[] highGradeFirst = [3, 1];
        int[] lowGradeFirst = [1, 3];

        double high = RetrievalMetrics.NdcgAtK(highGradeFirst, gold, k: 5);
        double low = RetrievalMetrics.NdcgAtK(lowGradeFirst, gold, k: 5);

        Assert.True(high > low);
    }

    [Fact]
    public void Irrelevant_top_hit_reduces_score()
    {
        int[] retrieved = [0, 3];
        int[] gold = [3];

        // DCG = 7 / log2(3) ; IDCG = 7 / log2(2) = 7.
        double expected = 7.0 / Math.Log2(3) / 7.0;

        double actual = RetrievalMetrics.NdcgAtK(retrieved, gold, k: 5);
        Assert.Equal(expected, actual, Tolerance);
        Assert.True(actual < 1.0);
    }

    [Fact]
    public void Relevant_item_beyond_k_is_truncated_to_zero()
    {
        int[] retrieved = [0, 0, 3];
        int[] gold = [3];
        Assert.Equal(0.0, RetrievalMetrics.NdcgAtK(retrieved, gold, k: 2), Tolerance);
    }

    [Fact]
    public void Relevant_item_within_k_after_truncation_scores_expected_value()
    {
        int[] retrieved = [0, 0, 3];
        int[] gold = [3];

        // DCG@3 = 7 / log2(4) = 3.5 ; IDCG@3 = 7 / log2(2) = 7 ; ratio = 0.5.
        Assert.Equal(0.5, RetrievalMetrics.NdcgAtK(retrieved, gold, k: 3), Tolerance);
    }

    [Fact]
    public void K_greater_than_result_count_uses_available_results()
    {
        int[] retrieved = [3];
        int[] gold = [3];
        Assert.Equal(1.0, RetrievalMetrics.NdcgAtK(retrieved, gold, k: 8), Tolerance);
    }

    [Fact]
    public void Known_value_is_computed_from_the_formula()
    {
        int[] retrieved = [3, 0, 1];
        int[] gold = [3, 1];

        // DCG = 7/log2(2) + 0/log2(3) + 1/log2(4) = 7 + 0 + 0.5 = 7.5
        // IDCG = 7/log2(2) + 1/log2(3) = 7 + 1/log2(3)
        double expected = 7.5 / (7.0 + (1.0 / Math.Log2(3)));

        double actual = RetrievalMetrics.NdcgAtK(retrieved, gold, k: 3);
        Assert.Equal(expected, actual, Tolerance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Invalid_k_is_rejected(int k)
    {
        int[] retrieved = [3];
        int[] gold = [3];
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalMetrics.NdcgAtK(retrieved, gold, k));
    }

    [Fact]
    public void Zero_idcg_is_rejected()
    {
        int[] retrieved = [0];
        int[] gold = [];
        Assert.Throws<ArgumentException>(() => RetrievalMetrics.NdcgAtK(retrieved, gold, k: 5));
    }
}
