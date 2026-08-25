using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using OpsFlow.Application.Documents;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// Extracts body text from DOCX documents using the Open XML SDK. Opens
/// read-only with a bounded <c>MaxCharactersInPart</c> to prevent
/// pathological XML expansion.
/// </summary>
public sealed class DocxTextExtractor : IDocumentTextExtractor
{
    internal const long MaxCharactersInPart = 10_000_000;
    internal const int MaxTraversalDepth = 256;

    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <inheritdoc />
    public bool CanExtract(string canonicalContentType) =>
        string.Equals(canonicalContentType, DocxContentType, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<DocumentTextExtractionResult> ExtractAsync(
        Stream content,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Office2007 models OpsFlow's actual markup capability: baseline
            // WordprocessingML text structure. Later-version mc:Choice branches
            // (w14, w15, …) correctly fall back via SDK MC processing.
            var settings = new OpenSettings
            {
                MaxCharactersInPart = MaxCharactersInPart,
                MarkupCompatibilityProcessSettings = new MarkupCompatibilityProcessSettings(
                    MarkupCompatibilityProcessMode.ProcessLoadedPartsOnly,
                    FileFormatVersions.Office2007),
            };

            using var document = WordprocessingDocument.Open(content, false, settings);

            var body = document.MainDocumentPart?.Document?.Body;
            if (body is null)
            {
                return Task.FromResult(
                    DocumentTextExtractionResult.Success(string.Empty));
            }

            var sb = new StringBuilder();
            bool first = true;
            bool afterParagraph = false;

            if (!WalkChildren(body, sb, maxCharacters, ref first, ref afterParagraph, depth: 0, cancellationToken))
            {
                return Task.FromResult(DocumentTextExtractionResult.LimitExceeded());
            }

            var normalized = TextNormalization.Normalize(sb.ToString());

            if (normalized.Length > maxCharacters)
            {
                return Task.FromResult(
                    DocumentTextExtractionResult.LimitExceeded());
            }

            return Task.FromResult(
                DocumentTextExtractionResult.Success(normalized));
        }
        catch (OperationCanceledException) { throw; }
        catch (OutOfMemoryException) { throw; }
        catch
        {
            return Task.FromResult(
                DocumentTextExtractionResult.MalformedDocument());
        }
    }

    internal static bool WalkChildren(
        OpenXmlElement parent,
        StringBuilder sb,
        int maxCharacters,
        ref bool first,
        ref bool afterParagraph,
        int depth,
        CancellationToken cancellationToken)
    {
        foreach (var child in parent.ChildElements)
        {
            if (!WalkElement(child, sb, maxCharacters, ref first, ref afterParagraph, depth, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private static bool WalkElement(
        OpenXmlElement element,
        StringBuilder sb,
        int maxCharacters,
        ref bool first,
        ref bool afterParagraph,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > MaxTraversalDepth)
        {
            return false;
        }

        if (element is Paragraph)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!first)
            {
                if (!TryAppendBounded(sb, '\n', maxCharacters))
                {
                    return false;
                }
            }

            first = false;
            afterParagraph = false;

            if (!WalkChildren(element, sb, maxCharacters, ref first, ref afterParagraph, depth + 1, cancellationToken))
            {
                return false;
            }

            afterParagraph = true;
            return true;
        }

        string? content = element switch
        {
            Text t => t.Text ?? string.Empty,
            TabChar => "\t",
            Break => "\n",
            CarriageReturn => "\n",
            NoBreakHyphen => "‑",
            SoftHyphen => "­",
            _ => null,
        };

        if (content is not null && content.Length > 0)
        {
            if (afterParagraph)
            {
                afterParagraph = false;
                if (!TryAppendBounded(sb, '\n', maxCharacters))
                {
                    return false;
                }
            }

            return TryAppendBounded(sb, content, maxCharacters);
        }

        return WalkChildren(element, sb, maxCharacters, ref first, ref afterParagraph, depth + 1, cancellationToken);
    }

    private static bool TryAppendBounded(StringBuilder sb, char value, int maxCharacters)
    {
        if (1 > maxCharacters - sb.Length)
        {
            return false;
        }

        _ = sb.Append(value);
        return true;
    }

    private static bool TryAppendBounded(StringBuilder sb, string value, int maxCharacters)
    {
        if (value.Length > maxCharacters - sb.Length)
        {
            return false;
        }

        _ = sb.Append(value);
        return true;
    }
}
