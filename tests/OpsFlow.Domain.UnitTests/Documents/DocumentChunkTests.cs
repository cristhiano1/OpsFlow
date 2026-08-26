using OpsFlow.Domain.Documents;

namespace OpsFlow.Domain.UnitTests.Documents;

public sealed class DocumentChunkTests
{
    private static readonly Guid ValidId = Guid.NewGuid();
    private static readonly Guid ValidDocumentId = Guid.NewGuid();

    [Fact]
    public void Constructor_with_valid_arguments_succeeds()
    {
        var chunk = new DocumentChunk(ValidId, ValidDocumentId, 0, 10, 15, "Hello");

        Assert.Equal(ValidId, chunk.Id);
        Assert.Equal(ValidDocumentId, chunk.DocumentId);
        Assert.Equal(0, chunk.ChunkIndex);
        Assert.Equal(10, chunk.StartOffset);
        Assert.Equal(15, chunk.EndOffset);
        Assert.Equal("Hello", chunk.Text);
    }

    [Fact]
    public void Constructor_rejects_empty_id()
    {
        Assert.Throws<ArgumentException>(() =>
            new DocumentChunk(Guid.Empty, ValidDocumentId, 0, 0, 5, "Hello"));
    }

    [Fact]
    public void Constructor_rejects_empty_document_id()
    {
        Assert.Throws<ArgumentException>(() =>
            new DocumentChunk(ValidId, Guid.Empty, 0, 0, 5, "Hello"));
    }

    [Fact]
    public void Constructor_rejects_negative_chunk_index()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentChunk(ValidId, ValidDocumentId, -1, 0, 5, "Hello"));
    }

    [Fact]
    public void Constructor_rejects_negative_start_offset()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentChunk(ValidId, ValidDocumentId, 0, -1, 5, "Hello!"));
    }

    [Fact]
    public void Constructor_rejects_end_offset_less_than_start_offset()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentChunk(ValidId, ValidDocumentId, 0, 10, 5, "Hello"));
    }

    [Fact]
    public void Constructor_rejects_null_text()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DocumentChunk(ValidId, ValidDocumentId, 0, 0, 5, null!));
    }

    [Fact]
    public void Constructor_rejects_text_length_mismatch()
    {
        Assert.Throws<ArgumentException>(() =>
            new DocumentChunk(ValidId, ValidDocumentId, 0, 0, 10, "short"));
    }

    [Fact]
    public void Constructor_rejects_text_exceeding_max_length()
    {
        var longText = new string('x', DocumentChunk.MaxTextLength + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentChunk(ValidId, ValidDocumentId, 0, 0, longText.Length, longText));
    }

    [Fact]
    public void Constructor_accepts_text_at_max_length()
    {
        var text = new string('x', DocumentChunk.MaxTextLength);
        var chunk = new DocumentChunk(ValidId, ValidDocumentId, 0, 0, text.Length, text);

        Assert.Equal(DocumentChunk.MaxTextLength, chunk.Text.Length);
    }

    [Fact]
    public void Constructor_accepts_zero_length_span()
    {
        var chunk = new DocumentChunk(ValidId, ValidDocumentId, 0, 5, 5, string.Empty);

        Assert.Equal(5, chunk.StartOffset);
        Assert.Equal(5, chunk.EndOffset);
        Assert.Equal(string.Empty, chunk.Text);
    }

    [Fact]
    public void MaxTextLength_is_1600()
    {
        Assert.Equal(1600, DocumentChunk.MaxTextLength);
    }
}
