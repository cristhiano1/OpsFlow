using System.Text;
using OpsFlow.Application.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Util;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// Extracts embedded text from PDF documents using a bounded content-order
/// algorithm that reproduces the default semantics of PdfPig's
/// <c>ContentOrderTextExtractor.GetText(page)</c> without materialising a
/// page-sized intermediate string.
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
                    if (!TryAppendBounded(sb, "\n\n", maxCharacters))
                    {
                        return Task.FromResult(DocumentTextExtractionResult.LimitExceeded());
                    }
                }

                firstPage = false;

                if (!AppendLettersBounded(page.Letters, sb, maxCharacters))
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
        catch (OperationCanceledException) { throw; }
        catch (OutOfMemoryException) { throw; }
        catch { return Task.FromResult(DocumentTextExtractionResult.MalformedDocument()); }
    }

    internal static bool TryAppendBounded(StringBuilder sb, char value, int max)
    {
        if (1 > max - sb.Length)
        {
            return false;
        }

        _ = sb.Append(value);
        return true;
    }

    internal static bool TryAppendBounded(StringBuilder sb, string value, int max)
    {
        if (value.Length > max - sb.Length)
        {
            return false;
        }

        _ = sb.Append(value);
        return true;
    }

    // Reproduces PdfPig 0.1.15 ContentOrderTextExtractor.GetText default
    // control flow (all Options false) with bounded output construction.
    internal static bool AppendLettersBounded(
        IEnumerable<Letter> letters,
        StringBuilder sb,
        int max)
    {
        Letter? previous = null;
        Letter? previousNonWhitespace = null;
        bool hasJustAddedWhitespace = false;

        foreach (var letter in letters)
        {
            if (string.IsNullOrEmpty(letter.Value))
            {
                continue;
            }

            // Dedicated space path — mirrors the reference's combined
            // condition: if (letter2.Value == " " && !flag).
            // When hasJustAddedWhitespace is true, space falls through to
            // the general body below (matching the reference).
            if (letter.Value == " " && !hasJustAddedWhitespace)
            {
                if (previous is not null && IsNewline(previous, letter))
                {
                    continue;
                }

                if (!TryAppendBounded(sb, ' ', max))
                {
                    return false;
                }

                previous = letter;
                hasJustAddedWhitespace = true;
                continue;
            }

            // General body: non-space letters, or space when
            // hasJustAddedWhitespace was already true.
            hasJustAddedWhitespace = false;

            if (previous is not null && letter.Value != " ")
            {
                if (IsNewline(previousNonWhitespace, letter))
                {
                    if (previous.Value == " " && sb.Length > 0)
                    {
                        sb.Length--;
                    }

                    if (!TryAppendBounded(sb, '\n', max))
                    {
                        return false;
                    }

                    hasJustAddedWhitespace = true;
                }
                else if (previous.Value != " ")
                {
                    var gap = letter.StartBaseLine.X - previous.EndBaseLine.X;
                    if (WhitespaceSizeStatistics.IsProbablyWhitespace(gap, previous))
                    {
                        if (!TryAppendBounded(sb, ' ', max))
                        {
                            return false;
                        }

                        hasJustAddedWhitespace = true;
                    }
                }
            }

            if (!TryAppendBounded(sb, letter.Value, max))
            {
                return false;
            }

            previous = letter;

            if (!string.IsNullOrWhiteSpace(letter.Value))
            {
                previousNonWhitespace = letter;
            }
        }

        return true;
    }

    // Mirrors PdfPig 0.1.15 ContentOrderTextExtractor.IsNewline with the
    // isDoubleNewline output omitted (SeparateParagraphsWithDoubleNewline
    // defaults false, so the caller never reads it).
    private static bool IsNewline(Letter? previous, Letter letter)
    {
        if (previous is null)
        {
            return false;
        }

        int prevSize = (int)Math.Round(previous.PointSize);
        int curSize = (int)Math.Round(letter.PointSize);
        int minSize = curSize < prevSize ? curSize : prevSize;
        double yGap = Math.Abs(previous.StartBaseLine.Y - letter.StartBaseLine.Y);

        return yGap > minSize * 0.9;
    }
}
