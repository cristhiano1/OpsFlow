using OpsFlow.Application.Documents;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class ReciprocalRankFusionTests
{
    private static readonly Guid DocId1 = Guid.NewGuid();
    private static readonly Guid DocId2 = Guid.NewGuid();
    private static readonly Guid DocId3 = Guid.NewGuid();

    private static SemanticChunkHit MakeSemantic(
        Guid? docId = null,
        Guid? chunkId = null,
        int chunkIndex = 0,
        int startOffset = 0,
        int endOffset = 5,
        string text = "hello",
        double cosineDistance = 0.1) =>
        new(docId ?? DocId1, chunkId ?? Guid.NewGuid(), chunkIndex, startOffset, endOffset, text, cosineDistance);

    private static LexicalChunkHit MakeLexical(
        Guid? docId = null,
        Guid? chunkId = null,
        int chunkIndex = 0,
        int startOffset = 0,
        int endOffset = 5,
        string text = "hello",
        int ftsRank = 100) =>
        new(docId ?? DocId1, chunkId ?? Guid.NewGuid(), chunkIndex, startOffset, endOffset, text, ftsRank);

    // ================================================================
    // Null / argument guards
    // ================================================================

    [Fact]
    public void Fuse_rejects_null_semantic_hits()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ReciprocalRankFusion.Fuse(null!, [], 10));
    }

    [Fact]
    public void Fuse_rejects_null_lexical_hits()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ReciprocalRankFusion.Fuse([], null!, 10));
    }

    [Fact]
    public void Fuse_rejects_topk_zero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReciprocalRankFusion.Fuse([], [], 0));
    }

    [Fact]
    public void Fuse_rejects_topk_negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReciprocalRankFusion.Fuse([], [], -1));
    }

    // ================================================================
    // Empty inputs
    // ================================================================

    [Fact]
    public void Fuse_returns_empty_when_both_lists_empty()
    {
        var result = ReciprocalRankFusion.Fuse([], [], 10);

        Assert.Empty(result);
    }

    [Fact]
    public void Fuse_returns_lexical_only_when_semantic_empty()
    {
        var chunkId = Guid.NewGuid();
        var lexical = new[] { MakeLexical(chunkId: chunkId) };

        var result = ReciprocalRankFusion.Fuse([], lexical, 10);

        Assert.Single(result);
        Assert.Equal(chunkId, result[0].DocumentChunkId);
        Assert.Null(result[0].SemanticRank);
        Assert.Equal(1, result[0].LexicalRank);
    }

    [Fact]
    public void Fuse_returns_semantic_only_when_lexical_empty()
    {
        var chunkId = Guid.NewGuid();
        var semantic = new[] { MakeSemantic(chunkId: chunkId) };

        var result = ReciprocalRankFusion.Fuse(semantic, [], 10);

        Assert.Single(result);
        Assert.Equal(chunkId, result[0].DocumentChunkId);
        Assert.Equal(1, result[0].SemanticRank);
        Assert.Null(result[0].LexicalRank);
    }

    // ================================================================
    // Single-source score verification
    // ================================================================

    [Fact]
    public void Fuse_semantic_only_hit_has_correct_score()
    {
        var semantic = new[] { MakeSemantic() };

        var result = ReciprocalRankFusion.Fuse(semantic, [], 10);

        Assert.Single(result);
        Assert.Equal(1.0 / (60 + 1), result[0].RrfScore);
    }

    [Fact]
    public void Fuse_lexical_only_hit_has_correct_score()
    {
        var lexical = new[] { MakeLexical() };

        var result = ReciprocalRankFusion.Fuse([], lexical, 10);

        Assert.Single(result);
        Assert.Equal(1.0 / (60 + 1), result[0].RrfScore);
    }

    // ================================================================
    // 1-based ranking
    // ================================================================

    [Fact]
    public void Fuse_uses_1_based_ranking()
    {
        var s1 = MakeSemantic(chunkId: Guid.NewGuid());
        var s2 = MakeSemantic(chunkId: Guid.NewGuid(), docId: DocId2);
        var semantic = new[] { s1, s2 };

        var result = ReciprocalRankFusion.Fuse(semantic, [], 10);

        Assert.Equal(2, result.Count);
        Assert.Equal(1.0 / (60 + 1), result[0].RrfScore);
        Assert.Equal(1.0 / (60 + 2), result[1].RrfScore);
        Assert.Equal(1, result[0].SemanticRank);
        Assert.Equal(2, result[1].SemanticRank);
    }

    // ================================================================
    // Disjoint lists
    // ================================================================

    [Fact]
    public void Fuse_disjoint_lists_produces_independent_scores()
    {
        var semChunkId = Guid.NewGuid();
        var lexChunkId = Guid.NewGuid();
        var semantic = new[] { MakeSemantic(chunkId: semChunkId) };
        var lexical = new[] { MakeLexical(chunkId: lexChunkId, docId: DocId2) };

        var result = ReciprocalRankFusion.Fuse(semantic, lexical, 10);

        Assert.Equal(2, result.Count);
        Assert.All(result, h => Assert.Equal(1.0 / (60 + 1), h.RrfScore));
    }

    // ================================================================
    // Overlapping chunk receives both contributions
    // ================================================================

    [Fact]
    public void Fuse_overlapping_chunk_receives_both_contributions()
    {
        var chunkId = Guid.NewGuid();
        var semantic = new[] { MakeSemantic(chunkId: chunkId) };
        var lexical = new[] { MakeLexical(chunkId: chunkId) };

        var result = ReciprocalRankFusion.Fuse(semantic, lexical, 10);

        Assert.Single(result);
        Assert.Equal((1.0 / (60 + 1)) + (1.0 / (60 + 1)), result[0].RrfScore);
        Assert.Equal(1, result[0].SemanticRank);
        Assert.Equal(1, result[0].LexicalRank);
    }

    // ================================================================
    // Exact RRF formula
    // ================================================================

    [Fact]
    public void Fuse_exact_formula_with_different_ranks()
    {
        var chunkId = Guid.NewGuid();
        var otherSemChunkId = Guid.NewGuid();
        var otherLexChunkId = Guid.NewGuid();

        var semantic = new[]
        {
            MakeSemantic(chunkId: otherSemChunkId, docId: DocId2, text: "other", endOffset: 5),
            MakeSemantic(chunkId: chunkId),
            MakeSemantic(chunkId: Guid.NewGuid(), docId: DocId3, text: "third", endOffset: 5),
        };
        var lexical = new[]
        {
            MakeLexical(chunkId: otherLexChunkId, docId: DocId3, text: "other2", endOffset: 6),
            MakeLexical(chunkId: Guid.NewGuid(), docId: DocId2, text: "fourth", endOffset: 6),
            MakeLexical(chunkId: chunkId),
        };

        var result = ReciprocalRankFusion.Fuse(semantic, lexical, 10);

        var fused = result.Single(h => h.DocumentChunkId == chunkId);
        Assert.Equal((1.0 / (60 + 2)) + (1.0 / (60 + 3)), fused.RrfScore);
        Assert.Equal(2, fused.SemanticRank);
        Assert.Equal(3, fused.LexicalRank);
    }

    // ================================================================
    // Ordering: higher score first
    // ================================================================

    [Fact]
    public void Fuse_higher_rrf_score_sorts_first()
    {
        var overlapChunkId = Guid.NewGuid();
        var singleChunkId = Guid.NewGuid();

        var semantic = new[] { MakeSemantic(chunkId: overlapChunkId) };
        var lexical = new[]
        {
            MakeLexical(chunkId: overlapChunkId),
            MakeLexical(chunkId: singleChunkId, docId: DocId2, text: "other", endOffset: 5),
        };

        var result = ReciprocalRankFusion.Fuse(semantic, lexical, 10);

        Assert.True(result[0].RrfScore > result[1].RrfScore);
        Assert.Equal(overlapChunkId, result[0].DocumentChunkId);
    }

    // ================================================================
    // Tie-break by best source rank
    // ================================================================

    [Fact]
    public void Fuse_tie_resolved_by_best_source_rank()
    {
        var chunkA = Guid.NewGuid();
        var chunkB = Guid.NewGuid();

        var semantic = new[]
        {
            MakeSemantic(chunkId: chunkA, text: "a", endOffset: 1),
            MakeSemantic(chunkId: chunkB, docId: DocId2, text: "b", endOffset: 1),
        };

        var result = ReciprocalRankFusion.Fuse(semantic, [], 10);

        Assert.Equal(chunkA, result[0].DocumentChunkId);
        Assert.Equal(chunkB, result[1].DocumentChunkId);
    }

    // ================================================================
    // Tie-break by DocumentChunkId
    // ================================================================

    [Fact]
    public void Fuse_final_tie_resolved_by_document_chunk_id()
    {
        var chunkLow = new Guid("00000000-0000-0000-0000-000000000001");
        var chunkHigh = new Guid("00000000-0000-0000-0000-000000000002");

        var semantic = new[]
        {
            MakeSemantic(chunkId: chunkHigh, text: "a", endOffset: 1),
        };
        var lexical = new[]
        {
            MakeLexical(chunkId: chunkLow, text: "b", endOffset: 1, docId: DocId2),
        };

        var result = ReciprocalRankFusion.Fuse(semantic, lexical, 10);

        Assert.Equal(2, result.Count);
        Assert.Equal(chunkLow, result[0].DocumentChunkId);
        Assert.Equal(chunkHigh, result[1].DocumentChunkId);
    }

    // ================================================================
    // TopK truncation
    // ================================================================

    [Fact]
    public void Fuse_truncates_to_topk()
    {
        var semantic = Enumerable.Range(0, 10)
            .Select(i => MakeSemantic(
                chunkId: Guid.NewGuid(),
                docId: Guid.NewGuid(),
                text: $"s{i}",
                endOffset: 2))
            .ToList();

        var result = ReciprocalRankFusion.Fuse(semantic, [], 3);

        Assert.Equal(3, result.Count);
        Assert.Equal(1.0 / (60 + 1), result[0].RrfScore);
        Assert.Equal(1.0 / (60 + 2), result[1].RrfScore);
        Assert.Equal(1.0 / (60 + 3), result[2].RrfScore);
    }

    // ================================================================
    // Duplicate detection — within source
    // ================================================================

    [Fact]
    public void Fuse_throws_on_duplicate_semantic_chunk_id()
    {
        var chunkId = Guid.NewGuid();
        var semantic = new[]
        {
            MakeSemantic(chunkId: chunkId),
            MakeSemantic(chunkId: chunkId),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ReciprocalRankFusion.Fuse(semantic, [], 10));
        Assert.Contains("Duplicate", ex.Message);
        Assert.Contains("semantic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fuse_throws_on_duplicate_lexical_chunk_id()
    {
        var chunkId = Guid.NewGuid();
        var lexical = new[]
        {
            MakeLexical(chunkId: chunkId),
            MakeLexical(chunkId: chunkId),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ReciprocalRankFusion.Fuse([], lexical, 10));
        Assert.Contains("Duplicate", ex.Message);
        Assert.Contains("lexical", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    // Metadata consistency — cross-source
    // ================================================================

    [Fact]
    public void Fuse_throws_on_inconsistent_document_id()
    {
        var chunkId = Guid.NewGuid();
        var semantic = new[] { MakeSemantic(chunkId: chunkId, docId: DocId1) };
        var lexical = new[] { MakeLexical(chunkId: chunkId, docId: DocId2) };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ReciprocalRankFusion.Fuse(semantic, lexical, 10));
        Assert.Contains("DocumentId", ex.Message);
    }

    [Fact]
    public void Fuse_throws_on_inconsistent_chunk_index()
    {
        var chunkId = Guid.NewGuid();
        var semantic = new[] { MakeSemantic(chunkId: chunkId, chunkIndex: 0) };
        var lexical = new[] { MakeLexical(chunkId: chunkId, chunkIndex: 1) };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ReciprocalRankFusion.Fuse(semantic, lexical, 10));
        Assert.Contains("ChunkIndex", ex.Message);
    }

    [Fact]
    public void Fuse_throws_on_inconsistent_start_offset()
    {
        var chunkId = Guid.NewGuid();
        var semantic = new[] { MakeSemantic(chunkId: chunkId, startOffset: 0) };
        var lexical = new[] { MakeLexical(chunkId: chunkId, startOffset: 10) };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ReciprocalRankFusion.Fuse(semantic, lexical, 10));
        Assert.Contains("StartOffset", ex.Message);
    }

    [Fact]
    public void Fuse_throws_on_inconsistent_end_offset()
    {
        var chunkId = Guid.NewGuid();
        var semantic = new[] { MakeSemantic(chunkId: chunkId, endOffset: 5) };
        var lexical = new[] { MakeLexical(chunkId: chunkId, endOffset: 10) };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ReciprocalRankFusion.Fuse(semantic, lexical, 10));
        Assert.Contains("EndOffset", ex.Message);
    }

    [Fact]
    public void Fuse_throws_on_inconsistent_text()
    {
        var chunkId = Guid.NewGuid();
        var semantic = new[] { MakeSemantic(chunkId: chunkId, text: "hello") };
        var lexical = new[] { MakeLexical(chunkId: chunkId, text: "world") };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ReciprocalRankFusion.Fuse(semantic, lexical, 10));
        Assert.Contains("Text", ex.Message);
    }

    // ================================================================
    // Input collections not mutated
    // ================================================================

    [Fact]
    public void Fuse_does_not_mutate_input_collections()
    {
        var semanticList = new List<SemanticChunkHit>
        {
            MakeSemantic(chunkId: Guid.NewGuid()),
            MakeSemantic(chunkId: Guid.NewGuid(), docId: DocId2, text: "world", endOffset: 5),
        };
        var lexicalList = new List<LexicalChunkHit>
        {
            MakeLexical(chunkId: Guid.NewGuid(), docId: DocId3, text: "third", endOffset: 5),
        };

        var semanticCopy = semanticList.ToList();
        var lexicalCopy = lexicalList.ToList();

        ReciprocalRankFusion.Fuse(semanticList, lexicalList, 10);

        Assert.Equal(semanticCopy.Count, semanticList.Count);
        Assert.Equal(lexicalCopy.Count, lexicalList.Count);
        for (int i = 0; i < semanticCopy.Count; i++)
        {
            Assert.Same(semanticCopy[i], semanticList[i]);
        }

        for (int i = 0; i < lexicalCopy.Count; i++)
        {
            Assert.Same(lexicalCopy[i], lexicalList[i]);
        }
    }
}
