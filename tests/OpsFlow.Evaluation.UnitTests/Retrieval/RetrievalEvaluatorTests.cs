using OpsFlow.Evaluation.Dataset;
using OpsFlow.Evaluation.Retrieval;

namespace OpsFlow.Evaluation.UnitTests.Retrieval;

public sealed class RetrievalEvaluatorTests
{
    private const double Tolerance = 1e-10;

    [Fact]
    public void Valid_multi_case_aggregate_computes_expected_means()
    {
        var dataset = Dataset(
            Case("case1", "q1?", ("c1", 3), ("c2", 1)),
            Case("case2", "q2?", ("c3", 2)));

        var results = Results(
            ("case1", Hits("c1", "c3", "c2")),
            ("case2", Hits("c3", "c1")));

        var result = RetrievalEvaluator.Evaluate(dataset, results);

        Assert.Equal(2, result.CaseCount);

        // case1 Recall@1 = 1 relevant (c1) / 2 total = 0.5 ; case2 Recall@1 = 1/1 = 1.0.
        var aggregateAt1 = result.Aggregates.Single(a => a.K == 1);
        Assert.Equal(0.75, aggregateAt1.MeanRecall, Tolerance);
    }

    [Fact]
    public void Canonical_k_values_produce_four_aggregate_rows()
    {
        var dataset = Dataset(Case("case1", "q?", ("c1", 3)));
        var results = Results(("case1", Hits("c1")));

        var result = RetrievalEvaluator.Evaluate(dataset, results);

        Assert.Equal(4, result.Aggregates.Count);
        Assert.Equal([1, 3, 5, 8], result.KValues);
    }

    [Fact]
    public void Unknown_ranked_chunk_is_rejected()
    {
        var dataset = Dataset(Case("case1", "q?", ("c1", 3)));
        var results = Results(("case1", Hits("c1", "ghost")));

        Assert.Throws<ArgumentException>(() => RetrievalEvaluator.Evaluate(dataset, results));
    }

    [Fact]
    public void Duplicate_ranked_chunk_is_rejected()
    {
        var dataset = Dataset(Case("case1", "q?", ("c1", 3)));
        var results = Results(("case1", Hits("c1", "c1")));

        Assert.Throws<ArgumentException>(() => RetrievalEvaluator.Evaluate(dataset, results));
    }

    [Fact]
    public void Duplicate_rank_is_rejected()
    {
        var dataset = Dataset(Case("case1", "q?", ("c1", 3)));
        IReadOnlyList<RetrievalEvaluationHit> hits =
            [new RetrievalEvaluationHit("c1", 1), new RetrievalEvaluationHit("c2", 1)];
        var results = Results(("case1", hits));

        Assert.Throws<ArgumentException>(() => RetrievalEvaluator.Evaluate(dataset, results));
    }

    [Fact]
    public void Rank_zero_is_rejected()
    {
        var dataset = Dataset(Case("case1", "q?", ("c1", 3)));
        IReadOnlyList<RetrievalEvaluationHit> hits = [new RetrievalEvaluationHit("c1", 0)];
        var results = Results(("case1", hits));

        Assert.Throws<ArgumentException>(() => RetrievalEvaluator.Evaluate(dataset, results));
    }

    [Fact]
    public void Rank_gap_is_rejected()
    {
        var dataset = Dataset(Case("case1", "q?", ("c1", 3)));
        IReadOnlyList<RetrievalEvaluationHit> hits =
            [new RetrievalEvaluationHit("c1", 1), new RetrievalEvaluationHit("c2", 3)];
        var results = Results(("case1", hits));

        Assert.Throws<ArgumentException>(() => RetrievalEvaluator.Evaluate(dataset, results));
    }

    [Fact]
    public void Rank_and_list_order_inconsistency_is_rejected()
    {
        var dataset = Dataset(Case("case1", "q?", ("c1", 3)));
        IReadOnlyList<RetrievalEvaluationHit> hits =
            [new RetrievalEvaluationHit("c1", 2), new RetrievalEvaluationHit("c2", 1)];
        var results = Results(("case1", hits));

        Assert.Throws<ArgumentException>(() => RetrievalEvaluator.Evaluate(dataset, results));
    }

    [Fact]
    public void Missing_ranked_result_set_is_rejected()
    {
        var dataset = Dataset(Case("case1", "q?", ("c1", 3)));
        var results = new Dictionary<string, IReadOnlyList<RetrievalEvaluationHit>>(StringComparer.Ordinal);

        Assert.Throws<ArgumentException>(() => RetrievalEvaluator.Evaluate(dataset, results));
    }

    [Fact]
    public void Known_but_irrelevant_corpus_chunk_scores_grade_zero()
    {
        var dataset = Dataset(Case("case1", "q?", ("c1", 3)));
        // c2 exists in the corpus but is not in the relevance map -> grade 0.
        var results = Results(("case1", Hits("c2", "c1")));

        var result = RetrievalEvaluator.Evaluate(dataset, results);
        var at1 = result.Cases[0].PerK.Single(p => p.K == 1);

        Assert.Equal(0.0, at1.Recall, Tolerance);
        Assert.Equal(0.0, at1.ReciprocalRank, Tolerance);
    }

    [Fact]
    public void Empty_ranked_result_is_valid_and_scores_zero()
    {
        var dataset = Dataset(Case("case1", "q?", ("c1", 3)));
        var results = Results(("case1", []));

        var result = RetrievalEvaluator.Evaluate(dataset, results);

        Assert.All(result.Cases[0].PerK, atK =>
        {
            Assert.Equal(0.0, atK.Recall, Tolerance);
            Assert.Equal(0.0, atK.ReciprocalRank, Tolerance);
            Assert.Equal(0.0, atK.Ndcg, Tolerance);
        });
    }

    // --- helpers ---

    private static EvaluationDataset Dataset(params EvaluationCase[] cases) =>
        new("1.0", "eval-ds", "desc",
        [
            new EvaluationDocument("doc", "Doc",
            [
                new EvaluationChunk("c1", "alpha"),
                new EvaluationChunk("c2", "beta"),
                new EvaluationChunk("c3", "gamma"),
                new EvaluationChunk("c4", "delta"),
            ]),
        ],
        cases);

    private static EvaluationCase Case(string caseId, string query, params (string ChunkKey, int Grade)[] relevance)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (chunkKey, grade) in relevance)
        {
            map[chunkKey] = grade;
        }

        return new EvaluationCase(caseId, query, map);
    }

    private static List<RetrievalEvaluationHit> Hits(params string[] chunkKeys)
    {
        var list = new List<RetrievalEvaluationHit>(chunkKeys.Length);
        for (int i = 0; i < chunkKeys.Length; i++)
        {
            list.Add(new RetrievalEvaluationHit(chunkKeys[i], i + 1));
        }

        return list;
    }

    private static Dictionary<string, IReadOnlyList<RetrievalEvaluationHit>> Results(
        params (string CaseId, IReadOnlyList<RetrievalEvaluationHit> Hits)[] entries)
    {
        var map = new Dictionary<string, IReadOnlyList<RetrievalEvaluationHit>>(StringComparer.Ordinal);
        foreach (var (caseId, hits) in entries)
        {
            map[caseId] = hits;
        }

        return map;
    }
}
