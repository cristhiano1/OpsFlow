namespace OpsFlow.Evaluation.Retrieval;

/// <summary>
/// A single ranked retrieval result expressed in neutral evaluation terms: the
/// symbolic <see cref="ChunkKey"/> that was retrieved and its 1-based
/// <see cref="Rank"/>. This type deliberately has no dependency on the
/// production retrieval pipeline (no <c>HybridChunkHit</c>, RRF, SQL, or
/// provider types), so the same evaluator can score a hybrid-RRF baseline today
/// and a reranked pipeline in a later PR without any change.
/// </summary>
/// <param name="ChunkKey">Symbolic key of the retrieved chunk (maps to the dataset corpus).</param>
/// <param name="Rank">1-based rank position; a ranked list must be contiguous and unique.</param>
public sealed record RetrievalEvaluationHit(string ChunkKey, int Rank);
