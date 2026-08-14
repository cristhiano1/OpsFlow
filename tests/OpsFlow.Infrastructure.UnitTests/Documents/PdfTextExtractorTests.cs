using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Infrastructure.UnitTests.Documents;

public sealed class PdfTextExtractorTests
{
    private readonly PdfTextExtractor _sut = new();

    // ================================================================
    // CanExtract
    // ================================================================

    [Fact]
    public void CanExtract_true_for_application_pdf()
    {
        Assert.True(_sut.CanExtract("application/pdf"));
    }

    [Fact]
    public void CanExtract_case_insensitive()
    {
        Assert.True(_sut.CanExtract("APPLICATION/PDF"));
    }

    [Fact]
    public void CanExtract_false_for_other_types()
    {
        Assert.False(_sut.CanExtract("text/plain"));
    }

    // ================================================================
    // Malformed input
    // ================================================================

    [Fact]
    public async Task Returns_malformed_for_non_pdf_bytes()
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
