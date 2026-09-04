using OpsFlow.Application.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeLexicalChunkRetriever : ILexicalChunkRetriever
{
    public IReadOnlyList<LexicalChunkHit> RetrieveResult { get; set; } = [];
    public bool RetrieveCalled { get; private set; }
    public Guid? ReceivedOrganizationId { get; private set; }
    public Guid? ReceivedProjectId { get; private set; }
    public string? ReceivedQueryText { get; private set; }
    public int? ReceivedTopK { get; private set; }

    public Task<IReadOnlyList<LexicalChunkHit>> RetrieveAsync(
        Guid organizationId,
        Guid projectId,
        string queryText,
        int topK,
        CancellationToken cancellationToken)
    {
        RetrieveCalled = true;
        ReceivedOrganizationId = organizationId;
        ReceivedProjectId = projectId;
        ReceivedQueryText = queryText;
        ReceivedTopK = topK;

        return Task.FromResult(RetrieveResult);
    }
}
