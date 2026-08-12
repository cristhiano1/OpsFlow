using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeDocumentRepository : IDocumentRepository
{
    public IReadOnlyList<Document>? ListResult { get; set; }

    public Guid? ReceivedProjectId { get; private set; }

    public Guid? ReceivedOrganizationId { get; private set; }

    public Task<IReadOnlyList<Document>> ListByProjectAsync(
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        ReceivedProjectId = projectId;
        ReceivedOrganizationId = organizationId;
        IReadOnlyList<Document> result = ListResult ?? [];
        return Task.FromResult(result);
    }
}
