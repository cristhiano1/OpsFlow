namespace OpsFlow.Evaluation.Dataset;

/// <summary>
/// A single evaluation case: a natural-language query paired with graded gold
/// relevance judgments. Relevance maps a symbolic chunk key to an integer grade
/// in <c>[1, 3]</c> (1 = partially relevant, 2 = relevant, 3 = highly relevant).
/// Grade 0 (irrelevant) is expressed by omission — chunks absent from the map
/// are irrelevant to this query.
/// </summary>
/// <param name="CaseId">Symbolic identifier for this case, unique within the dataset.</param>
/// <param name="Query">The natural-language query issued to retrieval.</param>
/// <param name="Relevance">Symbolic chunk key to graded relevance (1..3); omission means grade 0.</param>
public sealed record EvaluationCase(
    string CaseId,
    string Query,
    IReadOnlyDictionary<string, int> Relevance);
