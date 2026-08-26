using OpsFlow.Domain.Documents;

namespace OpsFlow.Domain.UnitTests.Documents;

public sealed class DocumentChunkSetTests
{
    private static readonly Guid ValidDocumentId = Guid.NewGuid();
    private static readonly DateTimeOffset ValidTimestamp = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_with_valid_arguments_succeeds()
    {
        var chunkSet = new DocumentChunkSet(ValidDocumentId, 1, 5, ValidTimestamp);

        Assert.Equal(ValidDocumentId, chunkSet.DocumentId);
        Assert.Equal(1, chunkSet.ChunkingVersion);
        Assert.Equal(5, chunkSet.ChunkCount);
        Assert.Equal(ValidTimestamp, chunkSet.CreatedAt);
    }

    [Fact]
    public void Constructor_rejects_empty_document_id()
    {
        Assert.Throws<ArgumentException>(() =>
            new DocumentChunkSet(Guid.Empty, 1, 0, ValidTimestamp));
    }

    [Fact]
    public void Constructor_rejects_zero_chunking_version()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentChunkSet(ValidDocumentId, 0, 0, ValidTimestamp));
    }

    [Fact]
    public void Constructor_rejects_negative_chunking_version()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentChunkSet(ValidDocumentId, -1, 0, ValidTimestamp));
    }

    [Fact]
    public void Constructor_rejects_negative_chunk_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentChunkSet(ValidDocumentId, 1, -1, ValidTimestamp));
    }

    [Fact]
    public void Constructor_accepts_zero_chunk_count()
    {
        var chunkSet = new DocumentChunkSet(ValidDocumentId, 1, 0, ValidTimestamp);

        Assert.Equal(0, chunkSet.ChunkCount);
    }

    [Fact]
    public void Constructor_accepts_chunking_version_greater_than_one()
    {
        var chunkSet = new DocumentChunkSet(ValidDocumentId, 42, 3, ValidTimestamp);

        Assert.Equal(42, chunkSet.ChunkingVersion);
    }
}
