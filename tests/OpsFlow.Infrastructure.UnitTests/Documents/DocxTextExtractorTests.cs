using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Infrastructure.UnitTests.Documents;

public sealed class DocxTextExtractorTests
{
    private readonly DocxTextExtractor _sut = new();

    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    // Creates an in-memory DOCX containing a single paragraph whose run holds
    // the supplied child elements in document order.
    private static MemoryStream MakeDocxWithRun(params DocumentFormat.OpenXml.OpenXmlElement[] runChildren)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            var run = new Run();
            foreach (var child in runChildren)
            {
                run.AppendChild(child.CloneNode(true));
            }

            mainPart.Document = new Document(new Body(new Paragraph(run)));
            mainPart.Document.Save();
        }

        ms.Position = 0;
        return ms;
    }

    private static MemoryStream MakeDocxWithBodyContent(params OpenXmlElement[] bodyChildren)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            var body = new Body();
            foreach (var child in bodyChildren)
            {
                body.AppendChild(child.CloneNode(true));
            }

            mainPart.Document = new Document(body);
            mainPart.Document.Save();
        }

        ms.Position = 0;
        return ms;
    }

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

    // ================================================================
    // Tab / Break / CarriageReturn fidelity (P2 fix verification)
    // ================================================================

    [Fact]
    public async Task Tab_is_preserved_as_tab_character()
    {
        // Text("A") + TabChar + Text("B") must extract as "A\tB", not "AB"
        using var stream = MakeDocxWithRun(new Text("A"), new TabChar(), new Text("B"));
        var result = await _sut.ExtractAsync(stream, 100, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("A\tB", result.Text);
    }

    [Fact]
    public async Task Break_is_preserved_as_newline()
    {
        // Text("A") + Break + Text("B") must extract as "A\nB", not "AB"
        using var stream = MakeDocxWithRun(new Text("A"), new Break(), new Text("B"));
        var result = await _sut.ExtractAsync(stream, 100, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("A\nB", result.Text);
    }

    [Fact]
    public async Task CarriageReturn_is_preserved_as_newline()
    {
        // Text("A") + CarriageReturn + Text("B") must extract as "A\nB", not "AB"
        using var stream = MakeDocxWithRun(new Text("A"), new CarriageReturn(), new Text("B"));
        var result = await _sut.ExtractAsync(stream, 100, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("A\nB", result.Text);
    }

    [Fact]
    public async Task Mixed_tab_break_text_preserved_in_document_order()
    {
        // Text("A") + Tab + Text("B") + Break + Text("C") => "A\tB\nC"
        using var stream = MakeDocxWithRun(
            new Text("A"), new TabChar(), new Text("B"), new Break(), new Text("C"));
        var result = await _sut.ExtractAsync(stream, 100, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("A\tB\nC", result.Text);
    }

    // ================================================================
    // Depth-first traversal order (P2 fix verification)
    // ================================================================

    [Fact]
    public void Nested_paragraph_content_appears_in_document_order()
    {
        var body = new Body(new Paragraph(
            new Run(new Text("A")),
            new Paragraph(new Run(new Text("B"))),
            new Run(new Text("C"))));

        var sb = new StringBuilder();
        bool first = true;
        var result = DocxTextExtractor.WalkChildren(
            body, sb, 1_000_000, ref first, CancellationToken.None);

        Assert.True(result);
        var text = sb.ToString();
        Assert.Equal("A\nBC", text);
        Assert.True(text.IndexOf('A') < text.IndexOf('B'));
        Assert.True(text.IndexOf('B') < text.IndexOf('C'));
    }

    // ================================================================
    // Blank paragraph preservation
    // ================================================================

    [Fact]
    public async Task Blank_paragraph_between_content_paragraphs_is_preserved()
    {
        using var stream = MakeDocxWithBodyContent(
            new Paragraph(new Run(new Text("A"))),
            new Paragraph(),
            new Paragraph(new Run(new Text("B"))));

        var result = await _sut.ExtractAsync(stream, 1_000_000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("A\n\nB", result.Text);
    }

    [Fact]
    public async Task Two_consecutive_blank_paragraphs_are_preserved()
    {
        using var stream = MakeDocxWithBodyContent(
            new Paragraph(new Run(new Text("A"))),
            new Paragraph(),
            new Paragraph(),
            new Paragraph(new Run(new Text("B"))));

        var result = await _sut.ExtractAsync(stream, 1_000_000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("A\n\n\nB", result.Text);
    }

    [Fact]
    public async Task Leading_blank_paragraph_is_trimmed_by_normalization()
    {
        using var stream = MakeDocxWithBodyContent(
            new Paragraph(),
            new Paragraph(new Run(new Text("A"))));

        var result = await _sut.ExtractAsync(stream, 1_000_000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("A", result.Text);
    }

    [Fact]
    public async Task Trailing_blank_paragraph_is_trimmed_by_normalization()
    {
        using var stream = MakeDocxWithBodyContent(
            new Paragraph(new Run(new Text("A"))),
            new Paragraph());

        var result = await _sut.ExtractAsync(stream, 1_000_000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("A", result.Text);
    }

    // ================================================================
    // Table cell paragraph extraction
    // ================================================================

    [Fact]
    public async Task Table_cell_paragraphs_extracted_in_document_order()
    {
        using var stream = MakeDocxWithBodyContent(
            new Table(
                new TableRow(
                    new TableCell(
                        new Paragraph(new Run(new Text("CellA"))),
                        new Paragraph(new Run(new Text("CellB")))))));

        var result = await _sut.ExtractAsync(stream, 1_000_000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("CellA\nCellB", result.Text);
    }

    // ================================================================
    // Blank paragraph limit accounting
    // ================================================================

    [Fact]
    public async Task Blank_paragraph_separators_count_toward_character_limit()
    {
        // A / empty / B => "A\n\nB" = 4 characters
        using var atLimit = MakeDocxWithBodyContent(
            new Paragraph(new Run(new Text("A"))),
            new Paragraph(),
            new Paragraph(new Run(new Text("B"))));
        var successResult = await _sut.ExtractAsync(atLimit, 4, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, successResult.Outcome);
        Assert.Equal("A\n\nB", successResult.Text);

        using var oneLess = MakeDocxWithBodyContent(
            new Paragraph(new Run(new Text("A"))),
            new Paragraph(),
            new Paragraph(new Run(new Text("B"))));
        var limitResult = await _sut.ExtractAsync(oneLess, 3, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.LimitExceeded, limitResult.Outcome);
    }

    [Fact]
    public async Task Separator_characters_count_toward_character_limit()
    {
        // "A\tB" is 3 characters — exactly at limit succeeds, one below fails
        using var atLimit = MakeDocxWithRun(new Text("A"), new TabChar(), new Text("B"));
        var successResult = await _sut.ExtractAsync(atLimit, 3, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, successResult.Outcome);
        Assert.Equal("A\tB", successResult.Text);

        using var oneLess = MakeDocxWithRun(new Text("A"), new TabChar(), new Text("B"));
        var limitResult = await _sut.ExtractAsync(oneLess, 2, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.LimitExceeded, limitResult.Outcome);
    }
}
