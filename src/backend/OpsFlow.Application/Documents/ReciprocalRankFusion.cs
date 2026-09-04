namespace OpsFlow.Application.Documents;

/// <summary>
/// Pure Reciprocal Rank Fusion (RRF) component. Fuses pre-ranked semantic and
/// lexical hit lists into a single hybrid result using rank positions only.
/// No database, embedding, or DI dependencies.
/// </summary>
public static class ReciprocalRankFusion
{
    private const int RrfK = 60;

    /// <summary>
    /// Fuses two ranked hit lists using the RRF formula:
    /// <c>score = 1/(K + semanticRank) + 1/(K + lexicalRank)</c>
    /// where ranks are 1-based positions in each source list.
    /// </summary>
    public static IReadOnlyList<HybridChunkHit> Fuse(
        IReadOnlyList<SemanticChunkHit> semanticHits,
        IReadOnlyList<LexicalChunkHit> lexicalHits,
        int topK)
    {
        ArgumentNullException.ThrowIfNull(semanticHits);
        ArgumentNullException.ThrowIfNull(lexicalHits);

        if (topK < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(topK), "TopK must be >= 1.");
        }

        var candidates = new Dictionary<Guid, CandidateEntry>();

        for (int i = 0; i < semanticHits.Count; i++)
        {
            var hit = semanticHits[i];
            int rank = i + 1;

            if (!candidates.TryAdd(hit.DocumentChunkId, new CandidateEntry
            {
                DocumentId = hit.DocumentId,
                DocumentChunkId = hit.DocumentChunkId,
                ChunkIndex = hit.ChunkIndex,
                StartOffset = hit.StartOffset,
                EndOffset = hit.EndOffset,
                Text = hit.Text,
                RrfScore = 1.0 / (RrfK + rank),
                SemanticRank = rank,
            }))
            {
                throw new InvalidOperationException(
                    $"Duplicate DocumentChunkId '{hit.DocumentChunkId}' in semantic hit list at position {i}.");
            }
        }

        var seenLexical = new HashSet<Guid>();
        for (int i = 0; i < lexicalHits.Count; i++)
        {
            var hit = lexicalHits[i];
            int rank = i + 1;
            double contribution = 1.0 / (RrfK + rank);

            if (!seenLexical.Add(hit.DocumentChunkId))
            {
                throw new InvalidOperationException(
                    $"Duplicate DocumentChunkId '{hit.DocumentChunkId}' in lexical hit list at position {i}.");
            }

            if (candidates.TryGetValue(hit.DocumentChunkId, out var existing))
            {
                ValidateMetadataConsistency(existing, hit);
                existing.RrfScore += contribution;
                existing.LexicalRank = rank;
            }
            else
            {
                candidates.Add(hit.DocumentChunkId, new CandidateEntry
                {
                    DocumentId = hit.DocumentId,
                    DocumentChunkId = hit.DocumentChunkId,
                    ChunkIndex = hit.ChunkIndex,
                    StartOffset = hit.StartOffset,
                    EndOffset = hit.EndOffset,
                    Text = hit.Text,
                    RrfScore = contribution,
                    LexicalRank = rank,
                });
            }
        }

        var sorted = candidates.Values
            .OrderByDescending(c => c.RrfScore)
            .ThenBy(BestSourceRank)
            .ThenBy(c => c.DocumentChunkId)
            .Take(topK)
            .Select(c => new HybridChunkHit(
                c.DocumentId,
                c.DocumentChunkId,
                c.ChunkIndex,
                c.StartOffset,
                c.EndOffset,
                c.Text,
                c.RrfScore,
                c.SemanticRank,
                c.LexicalRank))
            .ToList();

        return sorted;
    }

    private static int BestSourceRank(CandidateEntry c) =>
        Math.Min(c.SemanticRank ?? int.MaxValue, c.LexicalRank ?? int.MaxValue);

    private static void ValidateMetadataConsistency(CandidateEntry existing, LexicalChunkHit lexical)
    {
        if (existing.DocumentId != lexical.DocumentId)
        {
            throw new InvalidOperationException(
                $"Chunk '{existing.DocumentChunkId}' has DocumentId '{existing.DocumentId}' in semantic " +
                $"but '{lexical.DocumentId}' in lexical results.");
        }

        if (existing.ChunkIndex != lexical.ChunkIndex)
        {
            throw new InvalidOperationException(
                $"Chunk '{existing.DocumentChunkId}' has ChunkIndex {existing.ChunkIndex} in semantic " +
                $"but {lexical.ChunkIndex} in lexical results.");
        }

        if (existing.StartOffset != lexical.StartOffset)
        {
            throw new InvalidOperationException(
                $"Chunk '{existing.DocumentChunkId}' has StartOffset {existing.StartOffset} in semantic " +
                $"but {lexical.StartOffset} in lexical results.");
        }

        if (existing.EndOffset != lexical.EndOffset)
        {
            throw new InvalidOperationException(
                $"Chunk '{existing.DocumentChunkId}' has EndOffset {existing.EndOffset} in semantic " +
                $"but {lexical.EndOffset} in lexical results.");
        }

        if (existing.Text != lexical.Text)
        {
            throw new InvalidOperationException(
                $"Chunk '{existing.DocumentChunkId}' has different Text in semantic and lexical results.");
        }
    }

    private sealed class CandidateEntry
    {
        public Guid DocumentId { get; init; }
        public Guid DocumentChunkId { get; init; }
        public int ChunkIndex { get; init; }
        public int StartOffset { get; init; }
        public int EndOffset { get; init; }
        public string Text { get; init; } = string.Empty;
        public double RrfScore { get; set; }
        public int? SemanticRank { get; init; }
        public int? LexicalRank { get; set; }
    }
}
