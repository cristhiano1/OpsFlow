using OpsFlow.Evaluation.Dataset;

namespace OpsFlow.Evaluation.UnitTests.Dataset;

public sealed class EvaluationDatasetValidatorTests
{
    [Fact]
    public void Valid_dataset_is_accepted()
    {
        var dataset = ValidDataset();
        EvaluationDatasetValidator.Validate(dataset);
    }

    [Fact]
    public void Duplicate_case_id_is_rejected()
    {
        var dataset = new EvaluationDataset(
            "1.0", "ds", "desc",
            Documents(),
            [
                Case("dup", "query a?", ("c1", 3)),
                Case("dup", "query b?", ("c2", 3)),
            ]);

        Assert.Throws<EvaluationDatasetException>(() => EvaluationDatasetValidator.Validate(dataset));
    }

    [Fact]
    public void Duplicate_document_key_is_rejected()
    {
        var dataset = new EvaluationDataset(
            "1.0", "ds", "desc",
            [
                new EvaluationDocument("doc", "Doc A", [new EvaluationChunk("c1", "one")]),
                new EvaluationDocument("doc", "Doc B", [new EvaluationChunk("c2", "two")]),
            ],
            [Case("case1", "query?", ("c1", 3))]);

        Assert.Throws<EvaluationDatasetException>(() => EvaluationDatasetValidator.Validate(dataset));
    }

    [Fact]
    public void Duplicate_chunk_key_in_same_document_is_rejected()
    {
        var dataset = new EvaluationDataset(
            "1.0", "ds", "desc",
            [
                new EvaluationDocument("doc", "Doc", [
                    new EvaluationChunk("c1", "one"),
                    new EvaluationChunk("c1", "one again"),
                ]),
            ],
            [Case("case1", "query?", ("c1", 3))]);

        Assert.Throws<EvaluationDatasetException>(() => EvaluationDatasetValidator.Validate(dataset));
    }

    [Fact]
    public void Duplicate_chunk_key_across_documents_is_rejected()
    {
        var dataset = new EvaluationDataset(
            "1.0", "ds", "desc",
            [
                new EvaluationDocument("docA", "Doc A", [new EvaluationChunk("shared", "one")]),
                new EvaluationDocument("docB", "Doc B", [new EvaluationChunk("shared", "two")]),
            ],
            [Case("case1", "query?", ("shared", 3))]);

        Assert.Throws<EvaluationDatasetException>(() => EvaluationDatasetValidator.Validate(dataset));
    }

    [Fact]
    public void Unknown_relevance_chunk_is_rejected()
    {
        var dataset = new EvaluationDataset(
            "1.0", "ds", "desc",
            Documents(),
            [Case("case1", "query?", ("does-not-exist", 3))]);

        Assert.Throws<EvaluationDatasetException>(() => EvaluationDatasetValidator.Validate(dataset));
    }

    [Fact]
    public void Grade_zero_is_rejected()
    {
        var dataset = SingleCaseDataset(("c1", 0));
        Assert.Throws<EvaluationDatasetException>(() => EvaluationDatasetValidator.Validate(dataset));
    }

    [Fact]
    public void Negative_grade_is_rejected()
    {
        var dataset = SingleCaseDataset(("c1", -1));
        Assert.Throws<EvaluationDatasetException>(() => EvaluationDatasetValidator.Validate(dataset));
    }

    [Fact]
    public void Grade_above_three_is_rejected()
    {
        var dataset = SingleCaseDataset(("c1", 4));
        Assert.Throws<EvaluationDatasetException>(() => EvaluationDatasetValidator.Validate(dataset));
    }

    [Fact]
    public void Blank_query_is_rejected()
    {
        var dataset = new EvaluationDataset(
            "1.0", "ds", "desc",
            Documents(),
            [Case("case1", "   ", ("c1", 3))]);

        Assert.Throws<EvaluationDatasetException>(() => EvaluationDatasetValidator.Validate(dataset));
    }

    [Fact]
    public void Case_with_no_relevance_is_rejected()
    {
        var dataset = new EvaluationDataset(
            "1.0", "ds", "desc",
            Documents(),
            [new EvaluationCase("case1", "query?", new Dictionary<string, int>())]);

        Assert.Throws<EvaluationDatasetException>(() => EvaluationDatasetValidator.Validate(dataset));
    }

    [Fact]
    public void Blank_dataset_version_is_rejected()
    {
        var dataset = new EvaluationDataset(
            " ", "ds", "desc", Documents(), [Case("case1", "query?", ("c1", 3))]);

        Assert.Throws<EvaluationDatasetException>(() => EvaluationDatasetValidator.Validate(dataset));
    }

    [Fact]
    public void Zero_documents_is_rejected()
    {
        var dataset = new EvaluationDataset(
            "1.0", "ds", "desc", [], [Case("case1", "query?", ("c1", 3))]);

        Assert.Throws<EvaluationDatasetException>(() => EvaluationDatasetValidator.Validate(dataset));
    }

    [Fact]
    public void Zero_cases_is_rejected()
    {
        var dataset = new EvaluationDataset("1.0", "ds", "desc", Documents(), []);
        Assert.Throws<EvaluationDatasetException>(() => EvaluationDatasetValidator.Validate(dataset));
    }

    [Fact]
    public void Blank_chunk_text_is_rejected()
    {
        var dataset = new EvaluationDataset(
            "1.0", "ds", "desc",
            [new EvaluationDocument("doc", "Doc", [new EvaluationChunk("c1", "  ")])],
            [Case("case1", "query?", ("c1", 3))]);

        Assert.Throws<EvaluationDatasetException>(() => EvaluationDatasetValidator.Validate(dataset));
    }

    // --- helpers ---

    private static IReadOnlyList<EvaluationDocument> Documents() =>
        [
            new EvaluationDocument("doc", "Doc", [
                new EvaluationChunk("c1", "chunk one text"),
                new EvaluationChunk("c2", "chunk two text"),
            ]),
        ];

    private static EvaluationCase Case(string caseId, string query, params (string ChunkKey, int Grade)[] relevance)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (chunkKey, grade) in relevance)
        {
            map[chunkKey] = grade;
        }

        return new EvaluationCase(caseId, query, map);
    }

    private static EvaluationDataset SingleCaseDataset((string ChunkKey, int Grade) relevance) =>
        new("1.0", "ds", "desc", Documents(), [Case("case1", "query?", relevance)]);

    private static EvaluationDataset ValidDataset() =>
        new("1.0", "ds", "desc", Documents(),
        [
            Case("case1", "first query?", ("c1", 3), ("c2", 1)),
            Case("case2", "second query?", ("c2", 2)),
        ]);
}
