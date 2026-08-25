using OpsFlow.Domain.Documents;

namespace OpsFlow.Domain.UnitTests.Documents;

public sealed class DocumentExtractionTests
{
    private static readonly Guid ValidDocumentId = Guid.NewGuid();
    private static readonly DateTimeOffset ValidTimestamp = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_with_valid_arguments_succeeds()
    {
        var extraction = new DocumentExtraction(ValidDocumentId, "Hello", ValidTimestamp);

        Assert.Equal(ValidDocumentId, extraction.DocumentId);
        Assert.Equal("Hello", extraction.ExtractedText);
        Assert.Equal(ValidTimestamp, extraction.ExtractedAt);
    }

    [Fact]
    public void Constructor_rejects_empty_document_id()
    {
        Assert.Throws<ArgumentException>(() =>
            new DocumentExtraction(Guid.Empty, "text", ValidTimestamp));
    }

    [Fact]
    public void Constructor_rejects_null_extracted_text()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DocumentExtraction(ValidDocumentId, null!, ValidTimestamp));
    }

    [Fact]
    public void Constructor_accepts_empty_string_extracted_text()
    {
        var extraction = new DocumentExtraction(ValidDocumentId, string.Empty, ValidTimestamp);

        Assert.Equal(string.Empty, extraction.ExtractedText);
    }
}
