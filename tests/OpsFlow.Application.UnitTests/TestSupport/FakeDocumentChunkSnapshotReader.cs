using OpsFlow.Application.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeDocumentChunkSnapshotReader : IDocumentChunkSnapshotReader
{
    public DocumentChunkSnapshot? GetByDocumentResult { get; set; }
    public bool GetByDocumentCalled { get; private set; }
    public Guid? ReceivedDocumentId { get; private set; }
    public Guid? ReceivedProjectId { get; private set; }
    public Guid? ReceivedOrganizationId { get; private set; }

    public Task<DocumentChunkSnapshot?> GetByDocumentAsync(
        Guid documentId,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        GetByDocumentCalled = true;
        ReceivedDocumentId = documentId;
        ReceivedProjectId = projectId;
        ReceivedOrganizationId = organizationId;
        return Task.FromResult(GetByDocumentResult);
    }
}
