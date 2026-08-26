using OpsFlow.Application.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeDocumentChunker : IDocumentChunker
{
    public IReadOnlyList<DocumentChunkSlice>? ChunkResult { get; set; }
    public bool ChunkCalled { get; private set; }
    public string? ReceivedText { get; private set; }

    public IReadOnlyList<DocumentChunkSlice> Chunk(string text)
    {
        ChunkCalled = true;
        ReceivedText = text;
        return ChunkResult ?? [];
    }
}
