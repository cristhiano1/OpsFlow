using OpsFlow.Application.Documents;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class DeterministicDocumentChunkerTests
{
    private readonly DeterministicDocumentChunker _chunker = new();

    // ================================================================
    // Constants
    // ================================================================

    [Fact]
    public void TargetCharacters_is_1200()
    {
        Assert.Equal(1200, DeterministicDocumentChunker.TargetCharacters);
    }

    [Fact]
    public void MaxCharacters_is_1600()
    {
        Assert.Equal(1600, DeterministicDocumentChunker.MaxCharacters);
    }

    [Fact]
    public void OverlapCharacters_is_200()
    {
        Assert.Equal(200, DeterministicDocumentChunker.OverlapCharacters);
    }

    [Fact]
    public void BoundaryScanBack_is_200()
    {
        Assert.Equal(200, DeterministicDocumentChunker.BoundaryScanBack);
    }

    // ================================================================
    // Null / Empty / Short
    // ================================================================

    [Fact]
    public void Chunk_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => _chunker.Chunk(null!));
    }

    [Fact]
    public void Chunk_returns_empty_for_empty_string()
    {
        var result = _chunker.Chunk(string.Empty);

        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_returns_single_slice_for_short_text()
    {
        var result = _chunker.Chunk("Hello, world!");

        Assert.Single(result);
        Assert.Equal(0, result[0].StartOffset);
        Assert.Equal(13, result[0].EndOffset);
    }

    [Fact]
    public void Chunk_returns_single_slice_for_text_at_target_length()
    {
        var text = new string('a', DeterministicDocumentChunker.TargetCharacters);

        var result = _chunker.Chunk(text);

        Assert.Single(result);
        Assert.Equal(0, result[0].StartOffset);
        Assert.Equal(text.Length, result[0].EndOffset);
    }

    // ================================================================
    // Double newline boundary
    // ================================================================

    [Fact]
    public void Chunk_splits_at_double_newline_after_target()
    {
        var before = new string('a', 1250);
        var text = before + "\n\n" + new string('b', 100);

        var result = _chunker.Chunk(text);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].StartOffset);
        Assert.Equal(1252, result[0].EndOffset);
    }

    // ================================================================
    // Single newline boundary
    // ================================================================

    [Fact]
    public void Chunk_splits_at_single_newline_when_no_double_newline()
    {
        var before = new string('a', 1250);
        var text = before + "\n" + new string('b', 500);

        var result = _chunker.Chunk(text);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].StartOffset);
        Assert.Equal(1251, result[0].EndOffset);
    }

    // ================================================================
    // Whitespace boundary
    // ================================================================

    [Fact]
    public void Chunk_splits_at_whitespace_when_no_newline()
    {
        var text = new string('a', 1500) + " " + new string('b', 500);

        var result = _chunker.Chunk(text);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].StartOffset);
        Assert.Equal(1501, result[0].EndOffset);
    }

    // ================================================================
    // Hard cut
    // ================================================================

    [Fact]
    public void Chunk_hard_cuts_at_max_when_no_boundary_found()
    {
        var text = new string('a', 3200);

        var result = _chunker.Chunk(text);

        Assert.Equal(3, result.Count);
        Assert.Equal(0, result[0].StartOffset);
        Assert.Equal(1600, result[0].EndOffset);
    }

    // ================================================================
    // Overlap
    // ================================================================

    [Fact]
    public void Consecutive_chunks_overlap_by_200_characters()
    {
        var text = new string('a', 1250) + "\n\n" + new string('b', 1250) + "\n\n" + new string('c', 100);

        var result = _chunker.Chunk(text);

        Assert.True(result.Count >= 2);

        for (int i = 1; i < result.Count; i++)
        {
            int overlapStart = result[i].StartOffset;
            int prevEnd = result[i - 1].EndOffset;
            int overlap = prevEnd - overlapStart;
            Assert.True(overlap >= 0, $"Chunk {i} starts after chunk {i - 1} ends");
        }
    }

    [Fact]
    public void Overlap_does_not_go_backward()
    {
        var text = new string('a', 3200);

        var result = _chunker.Chunk(text);

        for (int i = 1; i < result.Count; i++)
        {
            Assert.True(result[i].StartOffset > result[i - 1].StartOffset,
                $"Chunk {i} start ({result[i].StartOffset}) must be > chunk {i - 1} start ({result[i - 1].StartOffset})");
        }
    }

    // ================================================================
    // Coverage invariant
    // ================================================================

    [Fact]
    public void Chunks_cover_entire_text()
    {
        var text = new string('a', 5000);

        var result = _chunker.Chunk(text);

        Assert.Equal(0, result[0].StartOffset);
        Assert.Equal(text.Length, result[^1].EndOffset);

        for (int i = 1; i < result.Count; i++)
        {
            Assert.True(result[i].StartOffset < result[i - 1].EndOffset,
                $"Gap between chunk {i - 1} and {i}");
        }
    }

    // ================================================================
    // Slice text invariant
    // ================================================================

    [Fact]
    public void Slice_offsets_produce_correct_substrings()
    {
        var text = "Hello world. " + new string('x', 1300) + "\n\nSecond paragraph here.";

        var result = _chunker.Chunk(text);

        foreach (var slice in result)
        {
            var substring = text[slice.StartOffset..slice.EndOffset];
            Assert.True(substring.Length <= DeterministicDocumentChunker.MaxCharacters);
            Assert.True(substring.Length > 0);
        }
    }

    // ================================================================
    // No chunk exceeds max
    // ================================================================

    [Fact]
    public void No_chunk_exceeds_max_characters()
    {
        var text = new string('a', 10000);

        var result = _chunker.Chunk(text);

        foreach (var slice in result)
        {
            int length = slice.EndOffset - slice.StartOffset;
            Assert.True(length <= DeterministicDocumentChunker.MaxCharacters,
                $"Chunk length {length} exceeds max {DeterministicDocumentChunker.MaxCharacters}");
        }
    }

    // ================================================================
    // Determinism
    // ================================================================

    [Fact]
    public void Same_input_produces_same_output()
    {
        var text = "Hello " + new string('x', 2000) + " world\n\nparagraph two " + new string('y', 1500);

        var result1 = _chunker.Chunk(text);
        var result2 = _chunker.Chunk(text);

        Assert.Equal(result1.Count, result2.Count);
        for (int i = 0; i < result1.Count; i++)
        {
            Assert.Equal(result1[i].StartOffset, result2[i].StartOffset);
            Assert.Equal(result1[i].EndOffset, result2[i].EndOffset);
        }
    }

    // ================================================================
    // Surrogate pair safety
    // ================================================================

    [Fact]
    public void End_boundary_does_not_split_surrogate_pair()
    {
        var emoji = "\U0001F600";
        Assert.Equal(2, emoji.Length);

        var text = new string('a', 1599) + emoji + new string('b', 500);

        var result = _chunker.Chunk(text);

        foreach (var slice in result)
        {
            if (slice.EndOffset < text.Length)
            {
                Assert.False(char.IsLowSurrogate(text[slice.EndOffset]),
                    $"End offset {slice.EndOffset} splits a surrogate pair");
            }

            if (slice.StartOffset > 0)
            {
                Assert.False(char.IsLowSurrogate(text[slice.StartOffset]),
                    $"Start offset {slice.StartOffset} splits a surrogate pair");
            }
        }
    }

    [Fact]
    public void Start_boundary_does_not_split_surrogate_pair()
    {
        var emoji = "\U0001F600";

        var padding = new string('a', 1398);
        var text = padding + emoji + new string('b', 1500);

        var result = _chunker.Chunk(text);

        foreach (var slice in result)
        {
            if (slice.StartOffset > 0 && slice.StartOffset < text.Length)
            {
                Assert.False(char.IsLowSurrogate(text[slice.StartOffset]),
                    $"Start offset {slice.StartOffset} splits a surrogate pair");
            }
        }
    }

    // ================================================================
    // Boundary priority
    // ================================================================

    [Fact]
    public void Double_newline_takes_priority_over_single_newline()
    {
        var text = new string('a', 1210) + "\n" + new string('b', 30) + "\n\n" + new string('c', 500);

        var result = _chunker.Chunk(text);

        Assert.Equal(1243, result[0].EndOffset);
    }

    // ================================================================
    // Text exactly at MaxCharacters
    // ================================================================

    [Fact]
    public void Text_at_exactly_max_characters_produces_single_chunk()
    {
        var text = new string('a', DeterministicDocumentChunker.MaxCharacters);

        var result = _chunker.Chunk(text);

        Assert.Single(result);
        Assert.Equal(0, result[0].StartOffset);
        Assert.Equal(1600, result[0].EndOffset);
    }

    // ================================================================
    // Text just over target
    // ================================================================

    [Fact]
    public void Text_just_over_target_with_boundary_near_end()
    {
        var text = new string('a', 1300) + " " + new string('b', 100);

        var result = _chunker.Chunk(text);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].StartOffset);
        Assert.Equal(1301, result[0].EndOffset);
    }

    // ================================================================
    // Single character
    // ================================================================

    [Fact]
    public void Single_character_produces_single_chunk()
    {
        var result = _chunker.Chunk("X");

        Assert.Single(result);
        Assert.Equal(0, result[0].StartOffset);
        Assert.Equal(1, result[0].EndOffset);
    }

    // ================================================================
    // Bounded search regression — separators beyond maxEnd
    // ================================================================

    [Fact]
    public void Paragraph_separator_beyond_max_end_is_not_selected()
    {
        var text = new string('a', 3000) + "\n\n" + new string('b', 500);

        var result = _chunker.Chunk(text);

        Assert.Equal(DeterministicDocumentChunker.MaxCharacters, result[0].EndOffset);

        foreach (var slice in result)
        {
            int length = slice.EndOffset - slice.StartOffset;
            Assert.True(length <= DeterministicDocumentChunker.MaxCharacters,
                $"Chunk length {length} exceeds max {DeterministicDocumentChunker.MaxCharacters}");
        }
    }

    [Fact]
    public void Line_separator_beyond_max_end_is_not_selected()
    {
        var text = new string('a', 3000) + "\n" + new string('b', 500);

        var result = _chunker.Chunk(text);

        Assert.Equal(DeterministicDocumentChunker.MaxCharacters, result[0].EndOffset);

        foreach (var slice in result)
        {
            int length = slice.EndOffset - slice.StartOffset;
            Assert.True(length <= DeterministicDocumentChunker.MaxCharacters,
                $"Chunk length {length} exceeds max {DeterministicDocumentChunker.MaxCharacters}");
        }
    }
}
