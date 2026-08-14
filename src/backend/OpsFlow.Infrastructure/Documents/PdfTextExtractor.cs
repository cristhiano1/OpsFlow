using System.Text;
using OpsFlow.Application.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// Extracts embedded text from PDF documents using PdfPig's content-order
/// text extraction. Does not perform OCR — scanned/image-only PDFs yield
/// an empty string (valid successful extraction).
/// </summary>
public sealed class PdfTextExtractor : IDocumentTextExtractor
{
    /// <inheritdoc />
    public bool CanExtract(string canonicalContentType) =>
        string.Equals(canonicalContentType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<DocumentTextExtractionResult> ExtractAsync(
        Stream content,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var document = PdfDocument.Open(content);

            var sb = new StringBuilder();
            bool firstPage = true;

            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!firstPage)
                {
                    _ = sb.Append("\n\n");
                }

                firstPage = false;

                var pageText = ContentOrderTextExtractor.GetText(page);
                _ = sb.Append(pageText);

                if (sb.Length > maxCharacters)
                {
                    return Task.FromResult(DocumentTextExtractionResult.LimitExceeded());
                }
            }

            var normalized = TextNormalization.Normalize(sb.ToString());

            if (normalized.Length > maxCharacters)
            {
                return Task.FromResult(DocumentTextExtractionResult.LimitExceeded());
            }

            return Task.FromResult(DocumentTextExtractionResult.Success(normalized));
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
            return Task.FromResult(DocumentTextExtractionResult.MalformedDocument());
        }
    }
}
