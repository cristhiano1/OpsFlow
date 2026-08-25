using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeDocumentExtractionRepository : IDocumentExtractionRepository
{
    public DocumentExtraction? GetByDocumentResult { get; set; }
    public bool GetByDocumentCalled { get; private set; }
    public Guid? ReceivedGetDocumentId { get; private set; }
    public Guid? ReceivedGetProjectId { get; private set; }
    public Guid? ReceivedGetOrganizationId { get; private set; }

    public DocumentExtractionAddResult? AddIfAbsentResult { get; set; }
    public bool AddIfAbsentCalled { get; private set; }
    public DocumentExtraction? LastAddedExtraction { get; private set; }
    public Guid? ReceivedAddProjectId { get; private set; }
    public Guid? ReceivedAddOrganizationId { get; private set; }

    public Task<DocumentExtraction?> GetByDocumentAsync(
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

    public Task<DocumentExtractionAddResult> AddIfAbsentAsync(
        DocumentExtraction extraction,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        AddIfAbsentCalled = true;
        LastAddedExtraction = extraction;
        ReceivedAddProjectId = projectId;
        ReceivedAddOrganizationId = organizationId;

        return Task.FromResult(
            AddIfAbsentResult ?? DocumentExtractionAddResult.Added(extraction));
    }
}
