namespace OpsFlow.Evaluation.Dataset;

/// <summary>
/// A versioned, self-contained retrieval-evaluation dataset: a synthetic corpus
/// of documents/chunks plus graded evaluation cases. Deterministic and
/// git-versioned; contains only synthetic content and no customer data,
/// secrets, or database identifiers. It is a regression benchmark, not a
/// real-world RAG accuracy measurement.
/// </summary>
/// <param name="DatasetVersion">Schema/content version (for example "1.0").</param>
/// <param name="DatasetId">Stable dataset identifier.</param>
/// <param name="Description">Human-readable description including the benchmark's intent and limitations.</param>
/// <param name="Documents">The synthetic corpus documents.</param>
/// <param name="Cases">The evaluation cases with graded gold relevance.</param>
public sealed record EvaluationDataset(
    string DatasetVersion,
    string DatasetId,
    string Description,
    IReadOnlyList<EvaluationDocument> Documents,
    IReadOnlyList<EvaluationCase> Cases);
