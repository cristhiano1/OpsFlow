using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeDocumentChunkSetRepository : IDocumentChunkSetRepository
{
    public DocumentChunkSet? GetByDocumentResult { get; set; }
    public bool GetByDocumentCalled { get; private set; }
    public Guid? ReceivedGetDocumentId { get; private set; }
    public Guid? ReceivedGetProjectId { get; private set; }
    public Guid? ReceivedGetOrganizationId { get; private set; }

    public DocumentChunkSetAddResult? AddIfAbsentResult { get; set; }
    public bool AddIfAbsentCalled { get; private set; }
    public DocumentChunkSet? LastAddedChunkSet { get; private set; }
    public IReadOnlyList<DocumentChunk>? LastAddedChunks { get; private set; }
    public Guid? ReceivedAddProjectId { get; private set; }
    public Guid? ReceivedAddOrganizationId { get; private set; }

    public Task<DocumentChunkSet?> GetByDocumentAsync(
        Guid documentId,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        GetByDocumentCalled = true;
        ReceivedGetDocumentId = documentId;
        ReceivedGetProjectId = projectId;
        ReceivedGetOrganizationId = organizationId;
        return Task.FromResult(GetByDocumentResult);
    }

    public Task<DocumentChunkSetAddResult> AddIfAbsentAsync(
        DocumentChunkSet chunkSet,
        IReadOnlyList<DocumentChunk> chunks,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        AddIfAbsentCalled = true;
        LastAddedChunkSet = chunkSet;
        LastAddedChunks = chunks;
        ReceivedAddProjectId = projectId;
        ReceivedAddOrganizationId = organizationId;

        return Task.FromResult(
            AddIfAbsentResult ?? DocumentChunkSetAddResult.Added(chunkSet));
    }
}
