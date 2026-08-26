namespace OpsFlow.Application.Documents;

/// <summary>
/// Deterministic overlapping chunker. Produces chunks of approximately
/// <see cref="TargetCharacters"/> characters, never exceeding
/// <see cref="MaxCharacters"/>, with <see cref="OverlapCharacters"/>
/// overlap between consecutive chunks.
/// </summary>
public sealed class DeterministicDocumentChunker : IDocumentChunker
{
    /// <summary>Target number of characters per chunk.</summary>
    public const int TargetCharacters = 1200;

    /// <summary>Maximum number of characters per chunk (hard limit).</summary>
    public const int MaxCharacters = 1600;

    /// <summary>Number of characters that overlap between consecutive chunks.</summary>
    public const int OverlapCharacters = 200;

    /// <summary>Number of characters to scan backward when searching for a whitespace boundary.</summary>
    public const int BoundaryScanBack = 200;

    /// <inheritdoc />
    public IReadOnlyList<DocumentChunkSlice> Chunk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return [];
        }

        var slices = new List<DocumentChunkSlice>();
        int position = 0;

        while (position < text.Length)
        {
            int end = FindEnd(text, position);
            slices.Add(new DocumentChunkSlice(position, end));

            if (end >= text.Length)
            {
                break;
            }

            int nextStart = end - OverlapCharacters;
            if (nextStart <= position)
            {
                nextStart = end;
            }

            if (nextStart < text.Length && char.IsLowSurrogate(text[nextStart]))
            {
                nextStart--;
            }

            position = nextStart;
        }

        return slices;
    }

    private static int FindEnd(string text, int start)
    {
        int targetEnd = start + TargetCharacters;

        if (targetEnd >= text.Length)
        {
            return text.Length;
        }

        int maxEnd = start + MaxCharacters;
        if (maxEnd > text.Length)
        {
            maxEnd = text.Length;
        }

        int searchFrom = targetEnd;
        int windowLength = maxEnd - searchFrom;

        int doubleNewline = text.IndexOf("\n\n", searchFrom, windowLength, StringComparison.Ordinal);
        if (doubleNewline >= 0)
        {
            return EnsureSurrogateSafe(text, doubleNewline + 2);
        }

        int singleNewline = text.IndexOf('\n', searchFrom, windowLength);
        if (singleNewline >= 0)
        {
            return EnsureSurrogateSafe(text, singleNewline + 1);
        }

        int scanStart = maxEnd - 1;
        int scanLimit = maxEnd - BoundaryScanBack;
        if (scanLimit < targetEnd)
        {
            scanLimit = targetEnd;
        }

        for (int i = scanStart; i >= scanLimit; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                int boundary = i + 1;
                return EnsureSurrogateSafe(text, boundary);
            }
        }

        return EnsureSurrogateSafe(text, maxEnd);
    }

    private static int EnsureSurrogateSafe(string text, int index)
    {
        if (index > 0 && index < text.Length && char.IsLowSurrogate(text[index]))
        {
            return index - 1;
        }

        return index;
    }
}
