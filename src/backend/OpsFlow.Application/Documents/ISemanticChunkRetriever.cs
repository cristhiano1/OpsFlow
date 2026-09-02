namespace OpsFlow.Application.Documents;

/// <summary>
/// Provider-neutral port for project-scoped semantic chunk retrieval.
/// Infrastructure provides the EF Core implementation.
/// </summary>
public interface ISemanticChunkRetriever
{
    /// <summary>
    /// Retrieves the nearest document chunks by cosine distance within a
    /// single project, filtered by embedding identity for compatibility.
    /// </summary>
    Task<IReadOnlyList<SemanticChunkHit>> RetrieveAsync(
        Guid organizationId,
        Guid projectId,
        EmbeddingGeneratorIdentity embeddingIdentity,
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        CancellationToken cancellationToken);
}
