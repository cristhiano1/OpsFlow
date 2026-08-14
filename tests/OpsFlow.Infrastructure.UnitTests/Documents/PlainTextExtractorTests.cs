using System.Text;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Infrastructure.UnitTests.Documents;

public sealed class PlainTextExtractorTests
{
    private readonly PlainTextExtractor _sut = new();

    private static MemoryStream Utf8Stream(string text) =>
        new(Encoding.UTF8.GetBytes(text));

    private static MemoryStream Utf8BomStream(string text)
    {
        var preamble = Encoding.UTF8.GetPreamble();
        var bytes = Encoding.UTF8.GetBytes(text);
        var combined = new byte[preamble.Length + bytes.Length];
        preamble.CopyTo(combined, 0);
        bytes.CopyTo(combined, preamble.Length);
        return new MemoryStream(combined);
    }

    // ================================================================
    // CanExtract
    // ================================================================

    [Fact]
    public void CanExtract_true_for_text_plain()
    {
        Assert.True(_sut.CanExtract("text/plain"));
    }

    [Fact]
    public void CanExtract_case_insensitive()
    {
        Assert.True(_sut.CanExtract("TEXT/PLAIN"));
    }

    [Fact]
    public void CanExtract_false_for_other_types()
    {
        Assert.False(_sut.CanExtract("application/pdf"));
    }

    // ================================================================
    // Basic extraction
    // ================================================================

    [Fact]
    public async Task Extracts_simple_text()
    {
        using var stream = Utf8Stream("hello world");
        var result = await _sut.ExtractAsync(stream, 1000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("hello world", result.Text);
    }

    [Fact]
    public async Task Extracts_empty_stream()
    {
        using var stream = Utf8Stream(string.Empty);
        var result = await _sut.ExtractAsync(stream, 1000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal(string.Empty, result.Text);
    }

    // ================================================================
    // BOM handling
    // ================================================================

    [Fact]
    public async Task Strips_utf8_bom()
    {
        using var stream = Utf8BomStream("hello");
        var result = await _sut.ExtractAsync(stream, 1000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("hello", result.Text);
    }

    // ================================================================
    // Unicode
    // ================================================================

    [Fact]
    public async Task Handles_unicode_characters()
    {
        using var stream = Utf8Stream("café résumé é");
        var result = await _sut.ExtractAsync(stream, 1000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Contains("café", result.Text);
    }

    // ================================================================
    // Line endings
    // ================================================================

    [Fact]
    public async Task Normalizes_CRLF_to_LF()
    {
        using var stream = Utf8Stream("line1\r\nline2");
        var result = await _sut.ExtractAsync(stream, 1000, CancellationToken.None);

        Assert.Equal("line1\nline2", result.Text);
    }

    [Fact]
    public async Task Normalizes_standalone_CR_to_LF()
    {
        using var stream = Utf8Stream("line1\rline2");
        var result = await _sut.ExtractAsync(stream, 1000, CancellationToken.None);

        Assert.Equal("line1\nline2", result.Text);
    }

    [Fact]
    public async Task Preserves_LF()
    {
        using var stream = Utf8Stream("line1\nline2");
        var result = await _sut.ExtractAsync(stream, 1000, CancellationToken.None);

        Assert.Equal("line1\nline2", result.Text);
    }

    // ================================================================
    // NUL removal
    // ================================================================

    [Fact]
    public async Task Removes_NUL_characters()
    {
        using var stream = Utf8Stream("a\0b\0c");
        var result = await _sut.ExtractAsync(stream, 1000, CancellationToken.None);

        Assert.Equal("abc", result.Text);
    }

    // ================================================================
    // Internal blank lines preserved
    // ================================================================

    [Fact]
    public async Task Preserves_internal_blank_lines()
    {
        using var stream = Utf8Stream("para1\n\npara2");
        var result = await _sut.ExtractAsync(stream, 1000, CancellationToken.None);

        Assert.Equal("para1\n\npara2", result.Text);
    }

    // ================================================================
    // Outer trim
    // ================================================================

    [Fact]
    public async Task Trims_leading_and_trailing_whitespace()
    {
        using var stream = Utf8Stream("  hello  ");
        var result = await _sut.ExtractAsync(stream, 1000, CancellationToken.None);

        Assert.Equal("hello", result.Text);
    }

    // ================================================================
    // CRLF across buffer boundary
    // ================================================================

    [Fact]
    public async Task CRLF_split_across_buffer_boundary_produces_single_LF()
    {
        var prefix = new string('a', 8191);
        using var stream = Utf8Stream(prefix + "\r\n" + "tail");
        var result = await _sut.ExtractAsync(stream, 100_000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal(prefix + "\n" + "tail", result.Text);
    }

    // ================================================================
    // Multibyte UTF-8 across buffer boundary
    // ================================================================

    [Fact]
    public async Task Multibyte_char_split_across_byte_buffer_boundary_is_decoded()
    {
        var prefix = new string('x', 8190);
        var input = prefix + "€" + "end";
        using var stream = Utf8Stream(input);
        var result = await _sut.ExtractAsync(stream, 100_000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal(input, result.Text);
    }

    // ================================================================
    // Invalid UTF-8
    // ================================================================

    [Fact]
    public async Task Returns_malformed_for_invalid_utf8()
    {
        using var stream = new MemoryStream([0xFF, 0xFE, 0x80, 0x81]);
        var result = await _sut.ExtractAsync(stream, 1000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.MalformedDocument, result.Outcome);
    }

    // ================================================================
    // Limit enforcement
    // ================================================================

    [Fact]
    public async Task Returns_limit_exceeded_when_text_over_max()
    {
        var text = new string('a', 101);
        using var stream = Utf8Stream(text);
        var result = await _sut.ExtractAsync(stream, 100, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.LimitExceeded, result.Outcome);
    }

    [Fact]
    public async Task Returns_success_at_exact_limit()
    {
        var text = new string('a', 100);
        using var stream = Utf8Stream(text);
        var result = await _sut.ExtractAsync(stream, 100, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal(100, result.Text!.Length);
    }

    // ================================================================
    // Cancellation
    // ================================================================

    [Fact]
    public async Task Respects_cancellation()
    {
        using var stream = Utf8Stream("hello");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _sut.ExtractAsync(stream, 1000, cts.Token));
    }
}
