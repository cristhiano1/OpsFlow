using Microsoft.Data.SqlTypes;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// Infrastructure-only persistence entity for a single chunk's embedding vector.
/// Not exposed to Domain or Application — <see cref="SqlVector{T}"/> stays
/// contained within Infrastructure.
/// </summary>
internal sealed class DocumentChunkEmbeddingRow
{
    public Guid EmbeddingSetId { get; set; }
    public Guid DocumentChunkId { get; set; }
    public SqlVector<float> Embedding { get; set; }
}
