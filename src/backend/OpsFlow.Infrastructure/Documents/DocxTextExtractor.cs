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

            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Track whether this paragraph contributed any visible content so
                // the inter-paragraph '\n' separator is only emitted when needed.
                bool paragraphEmpty = true;

                foreach (var element in paragraph.Descendants<OpenXmlElement>())
                {
                    // Map only the run-level content elements that carry extractable
                    // text. Everything else (properties, styles, bookmarks, …) is
                    // intentionally skipped.
                    string? toAppend = element switch
                    {
                        Text t => t.Text ?? string.Empty,
                        TabChar => "\t",
                        Break => "\n",
                        CarriageReturn => "\n",
                        _ => null,
                    };

                    if (toAppend is null || toAppend.Length == 0)
                    {
                        continue;
                    }

                    // Emit the inter-paragraph separator on the first content element
                    // of each non-empty paragraph after the very first one.
                    if (paragraphEmpty)
                    {
                        paragraphEmpty = false;
                        if (!first)
                        {
                            _ = sb.Append('\n');
                            if (sb.Length > maxCharacters)
                            {
                                return Task.FromResult(
                                    DocumentTextExtractionResult.LimitExceeded());
                            }
                        }

                        first = false;
                    }

                    _ = sb.Append(toAppend);

                    if (sb.Length > maxCharacters)
                    {
                        return Task.FromResult(
                            DocumentTextExtractionResult.LimitExceeded());
                    }
                }
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
}
