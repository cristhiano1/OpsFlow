using OpsFlow.Evaluation.Metrics;

namespace OpsFlow.Evaluation.UnitTests.Metrics;

public sealed class RecallAtKTests
{
    private const double Tolerance = 1e-10;

    [Fact]
    public void Perfect_recall_returns_one()
    {
        int[] grades = [3, 2];
        Assert.Equal(1.0, RetrievalMetrics.RecallAtK(grades, totalRelevant: 2, k: 5), Tolerance);
    }

    [Fact]
    public void Partial_recall_counts_relevant_over_total()
    {
        int[] grades = [3, 0, 0];
        Assert.Equal(0.5, RetrievalMetrics.RecallAtK(grades, totalRelevant: 2, k: 3), Tolerance);
    }

    [Fact]
    public void Zero_relevant_retrieved_returns_zero()
    {
        int[] grades = [0, 0];
        Assert.Equal(0.0, RetrievalMetrics.RecallAtK(grades, totalRelevant: 2, k: 5), Tolerance);
    }

    [Fact]
    public void Relevant_item_beyond_k_is_not_counted()
    {
        int[] grades = [0, 0, 3];
        Assert.Equal(0.0, RetrievalMetrics.RecallAtK(grades, totalRelevant: 1, k: 2), Tolerance);
    }

    [Fact]
    public void Relevant_item_within_k_is_counted()
    {
        int[] grades = [0, 0, 3];
        Assert.Equal(1.0, RetrievalMetrics.RecallAtK(grades, totalRelevant: 1, k: 3), Tolerance);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Any_positive_grade_counts_as_relevant(int grade)
    {
        int[] grades = [grade];
        Assert.Equal(1.0, RetrievalMetrics.RecallAtK(grades, totalRelevant: 1, k: 1), Tolerance);
    }

    [Fact]
    public void K_greater_than_result_count_uses_available_results()
    {
        int[] grades = [3];
        Assert.Equal(1.0, RetrievalMetrics.RecallAtK(grades, totalRelevant: 1, k: 8), Tolerance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_k_is_rejected(int k)
    {
        int[] grades = [3];
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalMetrics.RecallAtK(grades, totalRelevant: 1, k: k));
    }

    [Fact]
    public void Zero_total_relevant_is_rejected()
    {
        int[] grades = [3];
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalMetrics.RecallAtK(grades, totalRelevant: 0, k: 5));
    }
}
