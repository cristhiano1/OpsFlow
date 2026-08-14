using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Infrastructure.UnitTests.Documents;

public sealed class DocxTextExtractorTests
{
    private readonly DocxTextExtractor _sut = new();

    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    // ================================================================
    // CanExtract
    // ================================================================

    [Fact]
    public void CanExtract_true_for_docx_content_type()
    {
        Assert.True(_sut.CanExtract(DocxContentType));
    }

    [Fact]
    public void CanExtract_case_insensitive()
    {
        Assert.True(_sut.CanExtract(DocxContentType.ToUpperInvariant()));
    }

    [Fact]
    public void CanExtract_false_for_other_types()
    {
        Assert.False(_sut.CanExtract("text/plain"));
    }

    // ================================================================
    // MaxCharactersInPart constant
    // ================================================================

    [Fact]
    public void MaxCharactersInPart_is_ten_million()
    {
        Assert.Equal(10_000_000, DocxTextExtractor.MaxCharactersInPart);
    }

    // ================================================================
    // Malformed input
    // ================================================================

    [Fact]
    public async Task Returns_malformed_for_non_docx_bytes()
    {
        using var stream = new MemoryStream([0x01, 0x02, 0x03, 0x04]);
        var result = await _sut.ExtractAsync(stream, 1_000_000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.MalformedDocument, result.Outcome);
    }

    [Fact]
    public async Task Returns_malformed_for_empty_stream()
    {
        using var stream = new MemoryStream([]);
        var result = await _sut.ExtractAsync(stream, 1_000_000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.MalformedDocument, result.Outcome);
    }

    // ================================================================
    // Cancellation
    // ================================================================

    [Fact]
    public async Task Pre_cancelled_token_throws_before_parsing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using var stream = new MemoryStream([0x01]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _sut.ExtractAsync(stream, 1_000_000, cts.Token));
    }
}
