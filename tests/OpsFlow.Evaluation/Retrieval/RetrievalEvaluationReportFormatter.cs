using System.Globalization;

namespace OpsFlow.Evaluation.Retrieval;

/// <summary>
/// Formats a <see cref="RetrievalEvaluationResult"/> as deterministic plain
/// text for console / test output. Formatting only — no persistence, no ANSI,
/// no external dependency. Always emits the honest-scope disclaimer so the
/// numbers are never mistaken for a real-world accuracy measurement.
/// </summary>
public static class RetrievalEvaluationReportFormatter
{
    /// <summary>Disclaimer stating what this benchmark is.</summary>
    public const string BaselineDisclaimer = "Deterministic synthetic retrieval regression baseline.";

    /// <summary>Disclaimer stating what this benchmark is not.</summary>
    public const string NotRealWorldDisclaimer = "Not a real-world RAG accuracy measurement.";

    /// <summary>Produces the formatted plain-text report.</summary>
    public static string Format(RetrievalEvaluationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = new List<string>
        {
            "OpsFlow Retrieval Evaluation",
            BaselineDisclaimer,
            NotRealWorldDisclaimer,
            string.Empty,
            $"Dataset: {result.DatasetId} (v{result.DatasetVersion})",
            $"Cases: {result.CaseCount.ToString(CultureInfo.InvariantCulture)}",
            string.Empty,
            "Aggregate",
            $"  {"K",-4}{"Recall",12}{"MRR",12}{"nDCG",12}",
        };

        foreach (var aggregate in result.Aggregates)
        {
            lines.Add(
                $"  {aggregate.K,-4}" +
                $"{Format4(aggregate.MeanRecall),12}" +
                $"{Format4(aggregate.MeanReciprocalRank),12}" +
                $"{Format4(aggregate.MeanNdcg),12}");
        }

        int summaryK = result.KValues.Count == 0 ? 0 : result.KValues[^1];
        lines.Add(string.Empty);
        lines.Add($"Per-case (K={summaryK.ToString(CultureInfo.InvariantCulture)})");
        lines.Add($"  {"caseId",-28}{"Recall",10}{"RR",10}{"nDCG",10}  retrieved(top-K, symbolic)");

        foreach (var caseResult in result.Cases)
        {
            var atK = FindAtK(caseResult, summaryK);
            string recall = atK is null ? "-" : Format4(atK.Recall);
            string reciprocal = atK is null ? "-" : Format4(atK.ReciprocalRank);
            string ndcg = atK is null ? "-" : Format4(atK.Ndcg);
            string retrieved = FormatRetrievedPrefix(caseResult.RetrievedChunkKeys, summaryK);

            lines.Add(
                $"  {Truncate(caseResult.CaseId, 28),-28}" +
                $"{recall,10}{reciprocal,10}{ndcg,10}  {retrieved}");
        }

        return string.Join("\n", lines);
    }

    private static RetrievalEvaluationAtKResult? FindAtK(RetrievalEvaluationCaseResult caseResult, int k)
    {
        foreach (var atK in caseResult.PerK)
        {
            if (atK.K == k)
            {
                return atK;
            }
        }

        return null;
    }

    private static string FormatRetrievedPrefix(IReadOnlyList<string> retrievedChunkKeys, int k)
    {
        int limit = Math.Min(k, retrievedChunkKeys.Count);
        if (limit <= 0)
        {
            return "[]";
        }

        return "[" + string.Join(", ", retrievedChunkKeys.Take(limit)) + "]";
    }

    private static string Format4(double value) =>
        value.ToString("F4", CultureInfo.InvariantCulture);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
