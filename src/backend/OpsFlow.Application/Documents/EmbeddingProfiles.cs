namespace OpsFlow.Application.Documents;

/// <summary>
/// Product-level embedding profile constants. Each profile defines an
/// embedding-space compatibility boundary — all embeddings under the same
/// profile are comparable via distance functions.
/// </summary>
public static class EmbeddingProfiles
{
    /// <summary>Profile identity for the v1 semantic embedding space.</summary>
    public const string SemanticV1Id = "opsflow-semantic-v1";

    /// <summary>Fixed dimension count for the v1 semantic profile.</summary>
    public const int SemanticV1Dimensions = 1536;
}
