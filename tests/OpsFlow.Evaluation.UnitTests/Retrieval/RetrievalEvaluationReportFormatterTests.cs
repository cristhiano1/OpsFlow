using OpsFlow.Evaluation.Dataset;
using OpsFlow.Evaluation.Retrieval;

namespace OpsFlow.Evaluation.UnitTests.Retrieval;

public sealed class RetrievalEvaluationReportFormatterTests
{
    [Fact]
    public void Report_contains_identity_metrics_and_disclaimers()
    {
        var dataset = new EvaluationDataset(
            "1.0", "report-ds", "desc",
            [new EvaluationDocument("doc", "Doc", [new EvaluationChunk("c1", "alpha")])],
            [new EvaluationCase("case1", "what is alpha?", new Dictionary<string, int> { ["c1"] = 3 })]);

        var results = new Dictionary<string, IReadOnlyList<RetrievalEvaluationHit>>(StringComparer.Ordinal)
        {
            ["case1"] = [new RetrievalEvaluationHit("c1", 1)],
        };

        var result = RetrievalEvaluator.Evaluate(dataset, results);
        string report = RetrievalEvaluationReportFormatter.Format(result);

        Assert.Contains("report-ds", report, StringComparison.Ordinal);
        Assert.Contains("1.0", report, StringComparison.Ordinal);
        Assert.Contains("Cases: 1", report, StringComparison.Ordinal);
        Assert.Contains("Recall", report, StringComparison.Ordinal);
        Assert.Contains("MRR", report, StringComparison.Ordinal);
        Assert.Contains("nDCG", report, StringComparison.Ordinal);
        Assert.Contains("case1", report, StringComparison.Ordinal);

        // K cut-offs appear in the aggregate table.
        Assert.Contains("  1", report, StringComparison.Ordinal);
        Assert.Contains("  8", report, StringComparison.Ordinal);

        // Mandatory honest-scope disclaimers.
        Assert.Contains(RetrievalEvaluationReportFormatter.BaselineDisclaimer, report, StringComparison.Ordinal);
        Assert.Contains(RetrievalEvaluationReportFormatter.NotRealWorldDisclaimer, report, StringComparison.Ordinal);
    }
}
