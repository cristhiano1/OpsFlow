namespace OpsFlow.Evaluation.Dataset;

/// <summary>
/// A synthetic document in the evaluation corpus. Groups one or more
/// <see cref="EvaluationChunk"/> values under a symbolic <see cref="DocumentKey"/>.
/// </summary>
/// <param name="DocumentKey">Symbolic identifier for this document, unique within the dataset.</param>
/// <param name="Title">Human-readable document title.</param>
/// <param name="Chunks">The chunks belonging to this document.</param>
public sealed record EvaluationDocument(
    string DocumentKey,
    string Title,
    IReadOnlyList<EvaluationChunk> Chunks);
