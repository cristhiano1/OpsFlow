using OpsFlow.Application.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeSemanticChunkRetriever : ISemanticChunkRetriever
{
    public IReadOnlyList<SemanticChunkHit> RetrieveResult { get; set; } = [];
    public bool RetrieveCalled { get; private set; }
    public Guid? ReceivedOrganizationId { get; private set; }
    public Guid? ReceivedProjectId { get; private set; }
    public EmbeddingGeneratorIdentity? ReceivedIdentity { get; private set; }
    public ReadOnlyMemory<float>? ReceivedQueryEmbedding { get; private set; }
    public int? ReceivedTopK { get; private set; }

    public Task<IReadOnlyList<SemanticChunkHit>> RetrieveAsync(
        Guid organizationId,
        Guid projectId,
        EmbeddingGeneratorIdentity embeddingIdentity,
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        CancellationToken cancellationToken)
    {
        RetrieveCalled = true;
        ReceivedOrganizationId = organizationId;
        ReceivedProjectId = projectId;
        ReceivedIdentity = embeddingIdentity;
        ReceivedQueryEmbedding = queryEmbedding;
        ReceivedTopK = topK;

        return Task.FromResult(RetrieveResult);
    }
}
