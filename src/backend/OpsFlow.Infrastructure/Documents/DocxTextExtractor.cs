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
    // 2x the application-level text limit; accounts for XML markup overhead
    // within a single Open XML part while still bounding memory usage for a
    // 25 MiB upload.
    internal const long MaxCharactersInPart = 10_000_000;

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

            var settings = new OpenSettings
            {
                MaxCharactersInPart = MaxCharactersInPart,
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

            if (!WalkChildren(body, sb, maxCharacters, ref first, cancellationToken))
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
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
        CancellationToken cancellationToken)
    {
        foreach (var child in parent.ChildElements)
        {
            if (!WalkElement(child, sb, maxCharacters, ref first, cancellationToken))
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
        CancellationToken cancellationToken)
    {
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

            return WalkChildren(element, sb, maxCharacters, ref first, cancellationToken);
        }

        string? content = element switch
        {
            Text t => t.Text ?? string.Empty,
            TabChar => "\t",
            Break => "\n",
            CarriageReturn => "\n",
            _ => null,
        };

        if (content is not null && content.Length > 0)
        {
            return TryAppendBounded(sb, content, maxCharacters);
        }

        return WalkChildren(element, sb, maxCharacters, ref first, cancellationToken);
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
