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
        using var stream = MakeDocxWithRun(new Text("A"), new TabChar(), new Text("B"));
        var result = await _sut.ExtractAsync(stream, 100, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("A\tB", result.Text);
    }

    [Fact]
    public async Task Break_is_preserved_as_newline()
    {
        using var stream = MakeDocxWithRun(new Text("A"), new Break(), new Text("B"));
        var result = await _sut.ExtractAsync(stream, 100, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("A\nB", result.Text);
    }

    [Fact]
    public async Task CarriageReturn_is_preserved_as_newline()
    {
        using var stream = MakeDocxWithRun(new Text("A"), new CarriageReturn(), new Text("B"));
        var result = await _sut.ExtractAsync(stream, 100, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("A\nB", result.Text);
    }

    [Fact]
    public async Task Mixed_tab_break_text_preserved_in_document_order()
    {
        using var stream = MakeDocxWithRun(
            new Text("A"), new TabChar(), new Text("B"), new Break(), new Text("C"));
        var result = await _sut.ExtractAsync(stream, 100, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("A\tB\nC", result.Text);
    }

    // ================================================================
    // Depth-first traversal order — nested paragraph boundary
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
        bool afterParagraph = false;
        var result = DocxTextExtractor.WalkChildren(
            body, sb, 1_000_000, ref first, ref afterParagraph, depth: 0, CancellationToken.None);

        Assert.True(result);
        var text = sb.ToString();
        Assert.Equal("A\nB\nC", text);
        Assert.True(text.IndexOf('A') < text.IndexOf('B'));
        Assert.True(text.IndexOf('B') < text.IndexOf('C'));
    }

    [Fact]
    public void Nested_empty_paragraph_produces_blank_boundary()
    {
        var body = new Body(new Paragraph(
            new Run(new Text("A")),
            new Paragraph(),
            new Run(new Text("C"))));

        var sb = new StringBuilder();
        bool first = true;
        bool afterParagraph = false;
        var result = DocxTextExtractor.WalkChildren(
            body, sb, 1_000_000, ref first, ref afterParagraph, depth: 0, CancellationToken.None);

        Assert.True(result);
        Assert.Equal("A\n\nC", sb.ToString());
    }

    [Fact]
    public void Consecutive_nested_paragraphs_with_trailing_content()
    {
        var body = new Body(new Paragraph(
            new Run(new Text("A")),
            new Paragraph(new Run(new Text("B"))),
            new Paragraph(new Run(new Text("C"))),
            new Run(new Text("D"))));

        var sb = new StringBuilder();
        bool first = true;
        bool afterParagraph = false;
        var result = DocxTextExtractor.WalkChildren(
            body, sb, 1_000_000, ref first, ref afterParagraph, depth: 0, CancellationToken.None);

        Assert.True(result);
        Assert.Equal("A\nB\nC\nD", sb.ToString());
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
    // AlternateContent — SDK MC processing
    // ================================================================

    [Fact]
    public async Task AlternateContent_w14_selects_fallback_under_office2007_target()
    {
        using var stream = MakeDocxWithAlternateContentXml(
            choiceText: "CHOICE",
            fallbackText: "FALLBACK",
            requiresPrefix: "w14",
            requiresNamespaceUri: "http://schemas.microsoft.com/office/word/2010/wordml");

        var result = await _sut.ExtractAsync(stream, 1_000_000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Contains("FALLBACK", result.Text);
        Assert.DoesNotContain("CHOICE", result.Text);
        Assert.Equal(1, result.Text!.Split("FALLBACK").Length - 1);
    }

    [Fact]
    public async Task AlternateContent_selects_fallback_for_unsupported_namespace()
    {
        using var stream = MakeDocxWithAlternateContentXml(
            choiceText: "CHOICE",
            fallbackText: "FALLBACK",
            requiresPrefix: "futurens",
            requiresNamespaceUri: "http://example.com/unsupported/2099/namespace");

        var result = await _sut.ExtractAsync(stream, 1_000_000, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Contains("FALLBACK", result.Text);
        Assert.DoesNotContain("CHOICE", result.Text);
    }

    private static MemoryStream MakeDocxWithAlternateContentXml(
        string choiceText, string fallbackText,
        string requiresPrefix, string requiresNamespaceUri)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            var xml =
                $"""
                 <?xml version="1.0" encoding="utf-8"?>
                 <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                             xmlns:{requiresPrefix}="{requiresNamespaceUri}"
                             mc:Ignorable="{requiresPrefix}">
                   <w:body>
                     <w:p>
                       <mc:AlternateContent>
                         <mc:Choice Requires="{requiresPrefix}">
                           <w:r><w:t>{choiceText}</w:t></w:r>
                         </mc:Choice>
                         <mc:Fallback>
                           <w:r><w:t>{fallbackText}</w:t></w:r>
                         </mc:Fallback>
                       </mc:AlternateContent>
                     </w:p>
                   </w:body>
                 </w:document>
                 """;
            using var writer = new StreamWriter(mainPart.GetStream());
            writer.Write(xml);
        }

        ms.Position = 0;
        return ms;
    }

    // ================================================================
    // Limit accounting — blank paragraph boundaries
    // ================================================================

    [Fact]
    public async Task Blank_paragraph_separators_count_toward_character_limit()
    {
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
        using var atLimit = MakeDocxWithRun(new Text("A"), new TabChar(), new Text("B"));
        var successResult = await _sut.ExtractAsync(atLimit, 3, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, successResult.Outcome);
        Assert.Equal("A\tB", successResult.Text);

        using var oneLess = MakeDocxWithRun(new Text("A"), new TabChar(), new Text("B"));
        var limitResult = await _sut.ExtractAsync(oneLess, 2, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.LimitExceeded, limitResult.Outcome);
    }

    // ================================================================
    // Limit accounting — nested paragraph boundary
    // ================================================================

    [Fact]
    public void Nested_paragraph_boundary_counts_toward_character_limit()
    {
        // P(A, P(B), C) => "A\nB\nC" = 5 characters
        var body = new Body(new Paragraph(
            new Run(new Text("A")),
            new Paragraph(new Run(new Text("B"))),
            new Run(new Text("C"))));

        var sbOk = new StringBuilder();
        bool first1 = true;
        bool after1 = false;
        var ok = DocxTextExtractor.WalkChildren(
            body, sbOk, 5, ref first1, ref after1, depth: 0, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("A\nB\nC", sbOk.ToString());

        var sbFail = new StringBuilder();
        bool first2 = true;
        bool after2 = false;
        var exceeded = DocxTextExtractor.WalkChildren(
            body, sbFail, 4, ref first2, ref after2, depth: 0, CancellationToken.None);

        Assert.False(exceeded);
    }

    [Fact]
    public void Nested_empty_paragraph_boundary_counts_toward_character_limit()
    {
        // P(A, P(), C) => "A\n\nC" = 4 characters
        var body = new Body(new Paragraph(
            new Run(new Text("A")),
            new Paragraph(),
            new Run(new Text("C"))));

        var sbOk = new StringBuilder();
        bool first1 = true;
        bool after1 = false;
        var ok = DocxTextExtractor.WalkChildren(
            body, sbOk, 4, ref first1, ref after1, depth: 0, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("A\n\nC", sbOk.ToString());

        var sbFail = new StringBuilder();
        bool first2 = true;
        bool after2 = false;
        var exceeded = DocxTextExtractor.WalkChildren(
            body, sbFail, 3, ref first2, ref after2, depth: 0, CancellationToken.None);

        Assert.False(exceeded);
    }

    // ================================================================
    // MaxTraversalDepth constant
    // ================================================================

    [Fact]
    public void MaxTraversalDepth_is_256()
    {
        Assert.Equal(256, DocxTextExtractor.MaxTraversalDepth);
    }

    // ================================================================
    // Depth limit — traversal
    // ================================================================

    [Fact]
    public void Traversal_at_max_depth_succeeds()
    {
        // Chain: root → 254 inner Bodies → Paragraph → Run → Text
        // Text hits WalkElement at depth 255+1 = 256; 256 > 256 is false → passes
        var leaf = new Run(new Text("deep"));
        OpenXmlElement current = new Paragraph(leaf);
        for (int i = 0; i < 254; i++)
        {
            current = new Body(current);
        }

        var root = new Body(current);

        var sb = new StringBuilder();
        bool first = true;
        bool afterParagraph = false;
        var result = DocxTextExtractor.WalkChildren(
            root, sb, 1_000_000, ref first, ref afterParagraph, depth: 0, CancellationToken.None);

        Assert.True(result);
        Assert.Contains("deep", sb.ToString());
    }

    [Fact]
    public void Traversal_exceeding_max_depth_returns_false()
    {
        // Chain: root → 255 inner Bodies → Paragraph → Run → Text
        // Text hits WalkElement at depth 256+1 = 257; 257 > 256 is true → rejected
        var leaf = new Run(new Text("deep"));
        OpenXmlElement current = new Paragraph(leaf);
        for (int i = 0; i < 255; i++)
        {
            current = new Body(current);
        }

        var root = new Body(current);

        var sb = new StringBuilder();
        bool first = true;
        bool afterParagraph = false;
        var result = DocxTextExtractor.WalkChildren(
            root, sb, 1_000_000, ref first, ref afterParagraph, depth: 0, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public void Wide_but_shallow_document_is_not_affected_by_depth_limit()
    {
        var body = new Body();
        for (int i = 0; i < 500; i++)
        {
            body.AppendChild(new Paragraph(new Run(new Text($"P{i}"))));
        }

        var sb = new StringBuilder();
        bool first = true;
        bool afterParagraph = false;
        var result = DocxTextExtractor.WalkChildren(
            body, sb, 1_000_000, ref first, ref afterParagraph, depth: 0, CancellationToken.None);

        Assert.True(result);
        Assert.Contains("P0", sb.ToString());
        Assert.Contains("P499", sb.ToString());
    }

    // ================================================================
    // NoBreakHyphen / SoftHyphen fidelity (P2 fix verification)
    // ================================================================

    [Fact]
    public async Task NoBreakHyphen_is_preserved_as_non_breaking_hyphen()
    {
        using var stream = MakeDocxWithRun(new Text("A"), new NoBreakHyphen(), new Text("B"));
        var result = await _sut.ExtractAsync(stream, 100, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("A‑B", result.Text);
    }

    [Fact]
    public async Task SoftHyphen_is_preserved_as_soft_hyphen()
    {
        using var stream = MakeDocxWithRun(new Text("A"), new SoftHyphen(), new Text("B"));
        var result = await _sut.ExtractAsync(stream, 100, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, result.Outcome);
        Assert.Equal("A­B", result.Text);
    }

    // ================================================================
    // Limit accounting — hyphen elements
    // ================================================================

    [Fact]
    public async Task NoBreakHyphen_counts_toward_character_limit()
    {
        using var atLimit = MakeDocxWithRun(new Text("A"), new NoBreakHyphen(), new Text("B"));
        var successResult = await _sut.ExtractAsync(atLimit, 3, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, successResult.Outcome);
        Assert.Equal("A‑B", successResult.Text);

        using var oneLess = MakeDocxWithRun(new Text("A"), new NoBreakHyphen(), new Text("B"));
        var limitResult = await _sut.ExtractAsync(oneLess, 2, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.LimitExceeded, limitResult.Outcome);
    }

    [Fact]
    public async Task SoftHyphen_counts_toward_character_limit()
    {
        using var atLimit = MakeDocxWithRun(new Text("A"), new SoftHyphen(), new Text("B"));
        var successResult = await _sut.ExtractAsync(atLimit, 3, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.Success, successResult.Outcome);
        Assert.Equal("A­B", successResult.Text);

        using var oneLess = MakeDocxWithRun(new Text("A"), new SoftHyphen(), new Text("B"));
        var limitResult = await _sut.ExtractAsync(oneLess, 2, CancellationToken.None);

        Assert.Equal(DocumentTextExtractionOutcome.LimitExceeded, limitResult.Outcome);
    }
}
