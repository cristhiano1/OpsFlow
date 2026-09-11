using OpsFlow.Evaluation.Dataset;

namespace OpsFlow.Evaluation.UnitTests.Dataset;

public sealed class EvaluationDatasetLoaderTests
{
    private const string ValidJson = """
        {
          "datasetVersion": "1.0",
          "datasetId": "inline-test",
          "description": "inline",
          "documents": [
            { "documentKey": "doc", "title": "Doc", "chunks": [
              { "chunkKey": "c1", "text": "chunk one" },
              { "chunkKey": "c2", "text": "chunk two" }
            ] }
          ],
          "cases": [
            { "caseId": "case1", "query": "what is one?", "relevance": { "c1": 3, "c2": 1 } }
          ]
        }
        """;

    [Fact]
    public void Valid_json_loads_and_maps_fields()
    {
        var dataset = EvaluationDatasetLoader.LoadFromJson(ValidJson);

        Assert.Equal("1.0", dataset.DatasetVersion);
        Assert.Equal("inline-test", dataset.DatasetId);
        Assert.Single(dataset.Documents);
        Assert.Equal(2, dataset.Documents[0].Chunks.Count);
        Assert.Single(dataset.Cases);
        Assert.Equal(3, dataset.Cases[0].Relevance["c1"]);
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        Assert.Throws<EvaluationDatasetException>(
            () => EvaluationDatasetLoader.LoadFromJson("{ this is not valid json"));
    }

    [Fact]
    public void Semantic_validation_runs_automatically_after_load()
    {
        // Structurally valid JSON, but grade 5 violates the relevance contract.
        const string invalidGradeJson = """
            {
              "datasetVersion": "1.0",
              "datasetId": "inline-test",
              "description": "inline",
              "documents": [
                { "documentKey": "doc", "title": "Doc", "chunks": [
                  { "chunkKey": "c1", "text": "chunk one" }
                ] }
              ],
              "cases": [
                { "caseId": "case1", "query": "q?", "relevance": { "c1": 5 } }
              ]
            }
            """;

        Assert.Throws<EvaluationDatasetException>(
            () => EvaluationDatasetLoader.LoadFromJson(invalidGradeJson));
    }

    [Fact]
    public void Embedded_synthetic_v1_dataset_loads()
    {
        var dataset = EvaluationDatasetLoader.LoadSyntheticV1();

        Assert.Equal("opsflow-retrieval-synthetic-v1", dataset.DatasetId);
        Assert.Equal("1.0", dataset.DatasetVersion);
    }

    [Fact]
    public void Embedded_synthetic_v1_has_expected_shape()
    {
        var dataset = EvaluationDatasetLoader.LoadSyntheticV1();

        int chunkCount = dataset.Documents.Sum(document => document.Chunks.Count);

        Assert.Equal(7, dataset.Documents.Count);
        Assert.Equal(21, chunkCount);
        Assert.Equal(18, dataset.Cases.Count);
    }
}
