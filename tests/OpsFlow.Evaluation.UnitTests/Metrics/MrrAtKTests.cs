using OpsFlow.Evaluation.Metrics;

namespace OpsFlow.Evaluation.UnitTests.Metrics;

public sealed class MrrAtKTests
{
    private const double Tolerance = 1e-10;

    [Fact]
    public void First_result_relevant_returns_one()
    {
        int[] grades = [3, 0, 0];
        Assert.Equal(1.0, RetrievalMetrics.ReciprocalRankAtK(grades, k: 5), Tolerance);
    }

    [Fact]
    public void Relevant_at_rank_two_returns_one_half()
    {
        int[] grades = [0, 3, 0];
        Assert.Equal(0.5, RetrievalMetrics.ReciprocalRankAtK(grades, k: 5), Tolerance);
    }

    [Fact]
    public void Relevant_at_rank_three_returns_one_third()
    {
        int[] grades = [0, 0, 1];
        Assert.Equal(1.0 / 3.0, RetrievalMetrics.ReciprocalRankAtK(grades, k: 5), Tolerance);
    }

    [Fact]
    public void Relevant_beyond_k_returns_zero()
    {
        int[] grades = [0, 0, 3];
        Assert.Equal(0.0, RetrievalMetrics.ReciprocalRankAtK(grades, k: 2), Tolerance);
    }

    [Fact]
    public void No_relevant_result_returns_zero()
    {
        int[] grades = [0, 0, 0];
        Assert.Equal(0.0, RetrievalMetrics.ReciprocalRankAtK(grades, k: 3), Tolerance);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Any_positive_grade_counts_as_first_relevant(int grade)
    {
        int[] grades = [0, grade];
        Assert.Equal(0.5, RetrievalMetrics.ReciprocalRankAtK(grades, k: 5), Tolerance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Invalid_k_is_rejected(int k)
    {
        int[] grades = [3];
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalMetrics.ReciprocalRankAtK(grades, k));
    }

    [Fact]
    public void Mean_reciprocal_rank_across_cases_is_the_mean_of_per_case_values()
    {
        int[] caseA = [3, 0];      // reciprocal rank 1.0
        int[] caseB = [0, 2];      // reciprocal rank 0.5
        int[] caseC = [0, 0, 0];   // reciprocal rank 0.0

        double a = RetrievalMetrics.ReciprocalRankAtK(caseA, k: 5);
        double b = RetrievalMetrics.ReciprocalRankAtK(caseB, k: 5);
        double c = RetrievalMetrics.ReciprocalRankAtK(caseC, k: 5);

        double mrr = (a + b + c) / 3.0;
        Assert.Equal((1.0 + 0.5 + 0.0) / 3.0, mrr, Tolerance);
    }
}
