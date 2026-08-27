using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeDocumentEmbeddingSetRepository : IDocumentEmbeddingSetRepository
{
    public DocumentEmbeddingSet? GetByDocumentAndProfileResult { get; set; }
    public bool GetByDocumentAndProfileCalled { get; private set; }
    public Guid? ReceivedGetDocumentId { get; private set; }
    public string? ReceivedGetProfileId { get; private set; }
    public Guid? ReceivedGetProjectId { get; private set; }
    public Guid? ReceivedGetOrganizationId { get; private set; }

    public DocumentEmbeddingSetAddResult? AddIfAbsentResult { get; set; }
    public bool AddIfAbsentCalled { get; private set; }
    public DocumentEmbeddingSet? LastAddedEmbeddingSet { get; private set; }
    public IReadOnlyList<ChunkEmbeddingInput>? LastAddedEmbeddings { get; private set; }
    public Guid? ReceivedAddProjectId { get; private set; }
    public Guid? ReceivedAddOrganizationId { get; private set; }

    public Task<DocumentEmbeddingSet?> GetByDocumentAndProfileAsync(
        Guid documentId,
        string profileId,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        GetByDocumentAndProfileCalled = true;
        ReceivedGetDocumentId = documentId;
        ReceivedGetProfileId = profileId;
        ReceivedGetProjectId = projectId;
        ReceivedGetOrganizationId = organizationId;
        return Task.FromResult(GetByDocumentAndProfileResult);
    }

    public Task<DocumentEmbeddingSetAddResult> AddIfAbsentAsync(
        DocumentEmbeddingSet embeddingSet,
        IReadOnlyList<ChunkEmbeddingInput> embeddings,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        AddIfAbsentCalled = true;
        LastAddedEmbeddingSet = embeddingSet;
        LastAddedEmbeddings = embeddings;
        ReceivedAddProjectId = projectId;
        ReceivedAddOrganizationId = organizationId;

        return Task.FromResult(
            AddIfAbsentResult ?? DocumentEmbeddingSetAddResult.Added(embeddingSet));
    }
}
