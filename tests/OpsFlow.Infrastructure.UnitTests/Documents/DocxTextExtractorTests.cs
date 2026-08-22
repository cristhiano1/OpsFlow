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
    // Nested paragraph deduplication (P2 fix verification)
    // ================================================================

    // Verifies that ContentElements prunes the subtree rooted at any nested
    // Paragraph, so that text inside a text-box paragraph is not extracted
    // while processing the outer paragraph. We test this directly with an
    // in-memory element tree (no DOCX round-trip) because the Open XML SDK
    // does not re-type VML text-box content as Paragraph on deserialization.
    [Fact]
    public void ContentElements_prunes_nested_paragraph_subtree()
    {
        // Build an in-memory structure where an inner Paragraph is a direct
        // child of an outer Paragraph — the programmatic analogue of a VML
        // text-box paragraph nested inside its enclosing paragraph.
        var innerPara = new Paragraph(new Run(new Text("Inner")));
        var outerPara = new Paragraph(new Run(new Text("Outer")));
        outerPara.AppendChild(innerPara);

        // A. Outer traversal must NOT expose the nested paragraph's content.
        var outerElements = DocxTextExtractor.ContentElements(outerPara).ToList();
        Assert.Contains(outerElements, e => e is Text t && t.Text == "Outer");
        Assert.DoesNotContain(outerElements, e => e is Paragraph);
        Assert.DoesNotContain(outerElements, e => e is Text t && t.Text == "Inner");

        // B. The nested paragraph's own traversal DOES expose its text.
        var innerElements = DocxTextExtractor.ContentElements(innerPara).ToList();
        Assert.Contains(innerElements, e => e is Text t && t.Text == "Inner");
    }

    [Fact]
    public void Each_paragraph_contributes_text_exactly_once()
    {
        // Simulate body.Descendants<Paragraph>() visiting both paragraphs
        // and collecting text via ContentElements for each.
        var innerPara = new Paragraph(new Run(new Text("Inner")));
        var outerPara = new Paragraph(new Run(new Text("Outer")));
        outerPara.AppendChild(innerPara);

        var body = new Body(outerPara);
        var allText = new List<string>();

        foreach (var para in body.Descendants<Paragraph>())
        {
            foreach (var el in DocxTextExtractor.ContentElements(para))
            {
                if (el is Text t && !string.IsNullOrEmpty(t.Text))
                {
                    allText.Add(t.Text);
                }
            }
        }

        Assert.Equal(1, allText.Count(s => s == "Outer"));
        Assert.Equal(1, allText.Count(s => s == "Inner"));
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
