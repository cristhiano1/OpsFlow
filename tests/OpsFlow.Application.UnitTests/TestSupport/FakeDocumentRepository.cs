using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeDocumentRepository : IDocumentRepository
{
    public IReadOnlyList<Document>? ListResult { get; set; }

    public Guid? ReceivedProjectId { get; private set; }

    public Guid? ReceivedOrganizationId { get; private set; }

    public List<Document> Added { get; } = [];
    public Exception? AddException { get; set; }

    public int[] SharedCallOrder { get; set; } = [0];
    public int AddCallOrder { get; private set; }

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

    public Task AddAsync(Document document, CancellationToken cancellationToken)
    {
        if (AddException is not null)
        {
            throw AddException;
        }

        AddCallOrder = ++SharedCallOrder[0];
        Added.Add(document);
        return Task.CompletedTask;
    }
}
