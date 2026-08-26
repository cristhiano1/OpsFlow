namespace OpsFlow.Domain.Documents;

/// <summary>
/// A single text chunk produced by deterministic chunking of a
/// <see cref="DocumentExtraction"/>. Offsets are UTF-16 code-unit positions
/// into <see cref="DocumentExtraction.ExtractedText"/>: <see cref="StartOffset"/>
/// is inclusive, <see cref="EndOffset"/> is exclusive. The invariant
/// <c>Text.Length == EndOffset - StartOffset</c> is enforced by the constructor.
/// </summary>
public sealed class DocumentChunk
{
    /// <summary>Maximum number of characters allowed in a single chunk.</summary>
    public const int MaxTextLength = 1600;

    /// <summary>Unique identifier for this chunk.</summary>
    public Guid Id { get; private set; }

    /// <summary>The document this chunk belongs to.</summary>
    public Guid DocumentId { get; private set; }

    /// <summary>Zero-based position of this chunk within the chunk set.</summary>
    public int ChunkIndex { get; private set; }

    /// <summary>
    /// Inclusive UTF-16 code-unit offset into
    /// <see cref="DocumentExtraction.ExtractedText"/>.
    /// </summary>
    public int StartOffset { get; private set; }

    /// <summary>
    /// Exclusive UTF-16 code-unit offset into
    /// <see cref="DocumentExtraction.ExtractedText"/>.
    /// </summary>
    public int EndOffset { get; private set; }

    /// <summary>
    /// The chunk text. Must equal
    /// <c>extraction.ExtractedText[StartOffset..EndOffset]</c>.
    /// </summary>
    public string Text { get; private set; } = string.Empty;

    private DocumentChunk() { }

    /// <summary>Creates a new chunk with validated invariants.</summary>
    public DocumentChunk(
        Guid id,
        Guid documentId,
        int chunkIndex,
        int startOffset,
        int endOffset,
        string text)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Chunk ID must not be empty.", nameof(id));
        }

        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Document ID must not be empty.", nameof(documentId));
        }

        if (chunkIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex), "Chunk index must be >= 0.");
        }

        if (startOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startOffset), "Start offset must be >= 0.");
        }

        if (endOffset < startOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(endOffset), "End offset must be >= start offset.");
        }

        ArgumentNullException.ThrowIfNull(text);

        if (text.Length != endOffset - startOffset)
        {
            throw new ArgumentException(
                $"Text length ({text.Length}) must equal EndOffset - StartOffset ({endOffset - startOffset}).",
                nameof(text));
        }

        if (text.Length > MaxTextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(text),
                $"Text length ({text.Length}) exceeds maximum ({MaxTextLength}).");
        }

        Id = id;
        DocumentId = documentId;
        ChunkIndex = chunkIndex;
        StartOffset = startOffset;
        EndOffset = endOffset;
        Text = text;
    }
}
