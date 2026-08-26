using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of an idempotent chunk set insert. Encapsulates the duplicate-key
/// resolution so that Application code never references EF Core or SQL
/// provider details.
/// </summary>
public sealed class DocumentChunkSetAddResult
{
    /// <summary>Whether the chunk set was newly inserted.</summary>
    public bool WasAdded { get; private set; }

    /// <summary>The chunk set entity (newly inserted or the pre-existing row).</summary>
    public DocumentChunkSet ChunkSet { get; private set; } = null!;

    private DocumentChunkSetAddResult() { }

    /// <summary>The chunk set was newly inserted.</summary>
    public static DocumentChunkSetAddResult Added(DocumentChunkSet chunkSet)
    {
        ArgumentNullException.ThrowIfNull(chunkSet);
        return new DocumentChunkSetAddResult { WasAdded = true, ChunkSet = chunkSet };
    }

    /// <summary>A chunk set for this document already existed.</summary>
    public static DocumentChunkSetAddResult AlreadyExists(DocumentChunkSet chunkSet)
    {
        ArgumentNullException.ThrowIfNull(chunkSet);
        return new DocumentChunkSetAddResult { WasAdded = false, ChunkSet = chunkSet };
    }
}
