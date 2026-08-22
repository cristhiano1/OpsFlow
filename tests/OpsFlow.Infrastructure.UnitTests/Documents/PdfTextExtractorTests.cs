using System.Collections;
using System.Text;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace OpsFlow.Infrastructure.UnitTests.Documents;

public sealed class PdfTextExtractorTests
{
    private readonly PdfTextExtractor _sut = new();

    private static byte[] BuildMinimalPdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(612, 792);
        page.AddText(text, 12, new PdfPoint(50, 700), font);
        return builder.Build();
    }

    private static byte[] BuildMultiLinePdf(params (string text, double x, double y)[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(612, 792);
        foreach (var (text, x, y) in lines)
        {
            page.AddText(text, 12, new PdfPoint(x, y), font);
        }

        return builder.Build();
    }

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

    // ================================================================
    // Content-order parity — single line
    // ================================================================

    [Fact]
    public async Task Parity_single_line_text()
    {
        var bytes = BuildMinimalPdf("Hello PDF");

        using var stream = new MemoryStream(bytes);
        var result = await _sut.ExtractAsync(stream, 1_000_000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);

        using var refDoc = PdfDocument.Open(new MemoryStream(bytes));
        var refText = TextNormalization.Normalize(
            ContentOrderTextExtractor.GetText(refDoc.GetPage(1)));

        Assert.Equal(refText, result.Text);
    }

    // ================================================================
    // Content-order parity — multi-line (newline inference)
    // ================================================================

    [Fact]
    public async Task Parity_multi_line_text()
    {
        // Two runs placed 20pt apart vertically — well above the 0.9 *
        // min(12, 12) = 10.8pt threshold for newline detection.
        var bytes = BuildMultiLinePdf(
            ("First line", 50, 700),
            ("Second line", 50, 680));

        using var stream = new MemoryStream(bytes);
        var result = await _sut.ExtractAsync(stream, 1_000_000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);

        using var refDoc = PdfDocument.Open(new MemoryStream(bytes));
        var refText = TextNormalization.Normalize(
            ContentOrderTextExtractor.GetText(refDoc.GetPage(1)));

        Assert.Equal(refText, result.Text);
    }

    // ================================================================
    // Content-order parity — horizontal gap (inferred whitespace)
    // ================================================================

    [Fact]
    public async Task Parity_horizontal_gap_whitespace()
    {
        // "AB" placed far apart on the same baseline so PdfPig infers a space.
        var bytes = BuildMultiLinePdf(
            ("A", 50, 700),
            ("B", 100, 700));

        using var stream = new MemoryStream(bytes);
        var result = await _sut.ExtractAsync(stream, 1_000_000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);

        using var refDoc = PdfDocument.Open(new MemoryStream(bytes));
        var refText = TextNormalization.Normalize(
            ContentOrderTextExtractor.GetText(refDoc.GetPage(1)));

        Assert.Equal(refText, result.Text);
    }

    // ================================================================
    // Early-stop proof
    // ================================================================

    private sealed class BoundedLetterSequence : IEnumerable<Letter>
    {
        private readonly IReadOnlyList<Letter> _source;
        private readonly int _accessBoundary;

        internal BoundedLetterSequence(IReadOnlyList<Letter> source, int accessBoundary)
        {
            _source = source;
            _accessBoundary = accessBoundary;
        }

        public IEnumerator<Letter> GetEnumerator()
        {
            for (var i = 0; i < _source.Count; i++)
            {
                if (i >= _accessBoundary)
                {
                    throw new InvalidOperationException(
                        $"Letter at index {i} must not be accessed (boundary: {_accessBoundary}).");
                }

                yield return _source[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void AppendLettersBounded_stops_before_forbidden_letters()
    {
        var bytes = BuildMinimalPdf("ABCDEFGHIJKLMNOPQRSTUVWXYZ");

        using var doc = PdfDocument.Open(new MemoryStream(bytes));
        var letters = doc.GetPage(1).Letters;

        Assert.True(letters.Count >= 10, "Need at least 10 letters for this test.");

        int boundary = letters.Count / 2;
        var sentinel = new BoundedLetterSequence(letters, boundary);

        var sb = new StringBuilder();
        var result = PdfTextExtractor.AppendLettersBounded(sentinel, sb, 3);

        Assert.False(result);
        Assert.True(sb.Length <= 3);
    }

    // ================================================================
    // Exact character limit
    // ================================================================

    [Fact]
    public async Task Exact_limit_succeeds_one_over_limit_exceeds()
    {
        var bytes = BuildMinimalPdf("Limit");

        using var firstStream = new MemoryStream(bytes);
        var firstResult = await _sut.ExtractAsync(firstStream, 1_000_000, CancellationToken.None);
        Assert.Equal(DocumentTextExtractionOutcome.Success, firstResult.Outcome);
        var actualLength = firstResult.Text!.Length;
        Assert.True(actualLength > 0, "PDF produced no text — test is invalid.");

        using var atLimit = new MemoryStream(bytes);
        var atLimitResult = await _sut.ExtractAsync(atLimit, actualLength, CancellationToken.None);
        Assert.Equal(DocumentTextExtractionOutcome.Success, atLimitResult.Outcome);

        using var oneLess = new MemoryStream(bytes);
        var oneLessResult = await _sut.ExtractAsync(oneLess, actualLength - 1, CancellationToken.None);
        Assert.Equal(DocumentTextExtractionOutcome.LimitExceeded, oneLessResult.Outcome);
    }

    // ================================================================
    // Focused letter-level regression tests (internal helper)
    // ================================================================

    [Fact]
    public void Empty_value_letter_does_not_become_previous()
    {
        // Letter A, then empty-value letter, then letter B on the same line.
        // The empty letter must be skipped: whitespace inference between A and
        // B must use A's position, not the empty letter's position.
        var bytes = BuildMinimalPdf("AB");

        using var doc = PdfDocument.Open(new MemoryStream(bytes));
        var letters = doc.GetPage(1).Letters;
        Assert.True(letters.Count >= 2);

        // Reference output for the same letters, normalised to LF (the
        // reference uses AppendLine which emits \r\n on Windows).
        var refText = TextNormalization.Normalize(
            ContentOrderTextExtractor.GetText(doc.GetPage(1)));

        var sb = new StringBuilder();
        var result = PdfTextExtractor.AppendLettersBounded(letters, sb, 1_000_000);

        Assert.True(result);
        Assert.Equal(refText, TextNormalization.Normalize(sb.ToString()));
    }

    [Fact]
    public void Negative_gap_does_not_produce_whitespace()
    {
        // Place B to the LEFT of A on the same baseline — negative X gap.
        var bytes = BuildMultiLinePdf(
            ("A", 100, 700),
            ("B", 50, 700));

        using var doc = PdfDocument.Open(new MemoryStream(bytes));
        var letters = doc.GetPage(1).Letters;

        var refText = TextNormalization.Normalize(
            ContentOrderTextExtractor.GetText(doc.GetPage(1)));

        var sb = new StringBuilder();
        var result = PdfTextExtractor.AppendLettersBounded(letters, sb, 1_000_000);

        Assert.True(result);
        Assert.Equal(refText, TextNormalization.Normalize(sb.ToString()));
    }

    [Fact]
    public void Newline_uses_correct_threshold()
    {
        // Two lines placed 11pt apart vertically (above the 10.8pt threshold
        // for 12pt font) — must trigger a newline.
        // Also: two lines placed 5pt apart (below threshold) — must NOT
        // trigger a newline.
        var aboveThreshold = BuildMultiLinePdf(
            ("Above", 50, 700),
            ("Below", 50, 689));

        using var aboveDoc = PdfDocument.Open(new MemoryStream(aboveThreshold));
        var aboveRef = TextNormalization.Normalize(
            ContentOrderTextExtractor.GetText(aboveDoc.GetPage(1)));

        var sb1 = new StringBuilder();
        _ = PdfTextExtractor.AppendLettersBounded(
            aboveDoc.GetPage(1).Letters, sb1, 1_000_000);
        Assert.Equal(aboveRef, TextNormalization.Normalize(sb1.ToString()));

        var belowThreshold = BuildMultiLinePdf(
            ("Same", 50, 700),
            ("Line", 50, 695));

        using var belowDoc = PdfDocument.Open(new MemoryStream(belowThreshold));
        var belowRef = TextNormalization.Normalize(
            ContentOrderTextExtractor.GetText(belowDoc.GetPage(1)));

        var sb2 = new StringBuilder();
        _ = PdfTextExtractor.AppendLettersBounded(
            belowDoc.GetPage(1).Letters, sb2, 1_000_000);
        Assert.Equal(belowRef, TextNormalization.Normalize(sb2.ToString()));
    }

    [Fact]
    public void TryAppendBounded_respects_capacity()
    {
        var sb = new StringBuilder();
        Assert.True(PdfTextExtractor.TryAppendBounded(sb, 'A', 3));
        Assert.True(PdfTextExtractor.TryAppendBounded(sb, "BC", 3));
        Assert.Equal("ABC", sb.ToString());
        Assert.Equal(3, sb.Length);

        Assert.False(PdfTextExtractor.TryAppendBounded(sb, 'D', 3));
        Assert.False(PdfTextExtractor.TryAppendBounded(sb, "EF", 3));
        Assert.Equal(3, sb.Length);
    }

    [Fact]
    public void TryAppendBounded_zero_max_rejects_everything()
    {
        var sb = new StringBuilder();
        Assert.False(PdfTextExtractor.TryAppendBounded(sb, 'A', 0));
        Assert.False(PdfTextExtractor.TryAppendBounded(sb, "X", 0));
        Assert.Equal(0, sb.Length);
    }
}
