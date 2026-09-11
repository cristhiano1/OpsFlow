namespace OpsFlow.Evaluation.Dataset;

/// <summary>
/// A single retrievable chunk in the evaluation corpus. Identified by a stable
/// symbolic <see cref="ChunkKey"/> (never a database GUID) so gold labels remain
/// human-reviewable and decoupled from any runtime execution.
/// </summary>
/// <param name="ChunkKey">Globally-unique symbolic identifier for this chunk.</param>
/// <param name="Text">The chunk's text content.</param>
public sealed record EvaluationChunk(string ChunkKey, string Text);
