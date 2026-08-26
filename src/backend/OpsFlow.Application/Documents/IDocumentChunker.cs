namespace OpsFlow.Application.Documents;

/// <summary>
/// Splits normalized extraction text into deterministic, overlapping chunks.
/// The chunking algorithm is pure: same input always produces the same output.
/// </summary>
public interface IDocumentChunker
{
    /// <summary>
    /// Returns the ordered list of chunk slices for the given text.
    /// An empty string produces an empty list.
    /// </summary>
    IReadOnlyList<DocumentChunkSlice> Chunk(string text);
}
