using OpsFlow.Evaluation.Dataset;
using OpsFlow.Evaluation.Metrics;

namespace OpsFlow.Evaluation.Retrieval;

/// <summary>
/// Scores a set of ranked retrieval results against a validated
/// <see cref="EvaluationDataset"/>, producing per-case and aggregate metrics at
/// the configured rank cut-offs. The evaluator consumes only the neutral
/// <see cref="RetrievalEvaluationHit"/> abstraction, so a hybrid-RRF baseline
/// and a future reranked pipeline can be scored by the identical code path.
///
/// <para>
/// Malformed ranked results are rejected rather than silently repaired: a
/// ranked list must have contiguous 1-based ranks, unique chunk keys, and keys
/// that exist in the dataset corpus. A key that exists in the corpus but is
/// absent from a case's relevance map is valid and scores as grade 0.
/// </para>
/// </summary>
public static class RetrievalEvaluator
{
    /// <summary>Canonical rank cut-offs used for the retrieval baseline.</summary>
    public static readonly IReadOnlyList<int> CanonicalKValues = [1, 3, 5, 8];

    /// <summary>
    /// Evaluates every case in <paramref name="dataset"/> using the ranked
    /// results supplied per case id.
    /// </summary>
    /// <param name="dataset">A dataset that has already passed validation.</param>
    /// <param name="rankedResultsByCaseId">Ranked hits for each case, keyed by case id.</param>
    /// <param name="kValues">Rank cut-offs; defaults to <see cref="CanonicalKValues"/> when null.</param>
    public static RetrievalEvaluationResult Evaluate(
        EvaluationDataset dataset,
        IReadOnlyDictionary<string, IReadOnlyList<RetrievalEvaluationHit>> rankedResultsByCaseId,
        IReadOnlyList<int>? kValues = null)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(rankedResultsByCaseId);

        int[] resolvedK = ResolveKValues(kValues);
        var corpusChunkKeys = BuildCorpusChunkKeys(dataset);

        var caseResults = new List<RetrievalEvaluationCaseResult>(dataset.Cases.Count);
        foreach (var evaluationCase in dataset.Cases)
        {
            if (!rankedResultsByCaseId.TryGetValue(evaluationCase.CaseId, out var hits))
            {
                throw new ArgumentException(
                    $"No ranked result set was supplied for case '{evaluationCase.CaseId}'.",
                    nameof(rankedResultsByCaseId));
            }

            caseResults.Add(EvaluateCase(evaluationCase, hits, corpusChunkKeys, resolvedK));
        }

        var aggregates = Aggregate(caseResults, resolvedK);

        return new RetrievalEvaluationResult(
            dataset.DatasetId,
            dataset.DatasetVersion,
            caseResults.Count,
            resolvedK,
            caseResults,
            aggregates);
    }

    private static RetrievalEvaluationCaseResult EvaluateCase(
        EvaluationCase evaluationCase,
        IReadOnlyList<RetrievalEvaluationHit> hits,
        HashSet<string> corpusChunkKeys,
        int[] kValues)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var orderedKeys = ValidateAndExtractKeys(evaluationCase.CaseId, hits, corpusChunkKeys);

        var retrievedGrades = new List<int>(orderedKeys.Count);
        foreach (var chunkKey in orderedKeys)
        {
            retrievedGrades.Add(evaluationCase.Relevance.GetValueOrDefault(chunkKey, 0));
        }

        int totalRelevant = evaluationCase.Relevance.Count;
        int[] goldGrades = [.. evaluationCase.Relevance.Values];

        var perK = new List<RetrievalEvaluationAtKResult>(kValues.Length);
        foreach (int k in kValues)
        {
            perK.Add(new RetrievalEvaluationAtKResult(
                k,
                RetrievalMetrics.RecallAtK(retrievedGrades, totalRelevant, k),
                RetrievalMetrics.ReciprocalRankAtK(retrievedGrades, k),
                RetrievalMetrics.NdcgAtK(retrievedGrades, goldGrades, k)));
        }

        return new RetrievalEvaluationCaseResult(
            evaluationCase.CaseId,
            evaluationCase.Query,
            orderedKeys,
            evaluationCase.Relevance,
            perK);
    }

    private static List<string> ValidateAndExtractKeys(
        string caseId,
        IReadOnlyList<RetrievalEvaluationHit> hits,
        HashSet<string> corpusChunkKeys)
    {
        var orderedKeys = new List<string>(hits.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            if (hit.Rank != i + 1)
            {
                throw new ArgumentException(
                    $"Case '{caseId}' ranked hits must have contiguous 1-based ranks in order; " +
                    $"expected rank {i + 1} at position {i} but found {hit.Rank}.",
                    nameof(hits));
            }

            if (string.IsNullOrWhiteSpace(hit.ChunkKey))
            {
                throw new ArgumentException(
                    $"Case '{caseId}' has a blank chunk key at rank {hit.Rank}.", nameof(hits));
            }

            if (!corpusChunkKeys.Contains(hit.ChunkKey))
            {
                throw new ArgumentException(
                    $"Case '{caseId}' returned chunk key '{hit.ChunkKey}' which is not in the dataset corpus.",
                    nameof(hits));
            }

            if (!seen.Add(hit.ChunkKey))
            {
                throw new ArgumentException(
                    $"Case '{caseId}' returned duplicate chunk key '{hit.ChunkKey}'.", nameof(hits));
            }

            orderedKeys.Add(hit.ChunkKey);
        }

        return orderedKeys;
    }

    private static List<RetrievalEvaluationAggregateAtK> Aggregate(
        List<RetrievalEvaluationCaseResult> caseResults,
        int[] kValues)
    {
        var aggregates = new List<RetrievalEvaluationAggregateAtK>(kValues.Length);
        int caseCount = caseResults.Count;

        for (int index = 0; index < kValues.Length; index++)
        {
            int k = kValues[index];
            double recallSum = 0.0;
            double reciprocalRankSum = 0.0;
            double ndcgSum = 0.0;

            foreach (var caseResult in caseResults)
            {
                var atK = caseResult.PerK[index];
                recallSum += atK.Recall;
                reciprocalRankSum += atK.ReciprocalRank;
                ndcgSum += atK.Ndcg;
            }

            double divisor = caseCount == 0 ? 1.0 : caseCount;
            aggregates.Add(new RetrievalEvaluationAggregateAtK(
                k,
                caseCount,
                recallSum / divisor,
                reciprocalRankSum / divisor,
                ndcgSum / divisor));
        }

        return aggregates;
    }

    private static int[] ResolveKValues(IReadOnlyList<int>? kValues)
    {
        IReadOnlyList<int> source = kValues ?? CanonicalKValues;
        if (source.Count == 0)
        {
            throw new ArgumentException("At least one K value is required.", nameof(kValues));
        }

        foreach (int k in source)
        {
            if (k <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(kValues), k, "K values must be > 0.");
            }
        }

        return [.. source.Distinct().OrderBy(k => k)];
    }

    private static HashSet<string> BuildCorpusChunkKeys(EvaluationDataset dataset)
    {
        IEnumerable<string> chunkKeys = dataset.Documents
            .SelectMany(document => document.Chunks)
            .Select(chunk => chunk.ChunkKey);

        return new HashSet<string>(chunkKeys, StringComparer.Ordinal);
    }
}
