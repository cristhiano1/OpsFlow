using OpsFlow.Application.Documents;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class TextNormalizationTests
{
    [Fact]
    public void Throws_on_null()
    {
        Assert.Throws<ArgumentNullException>(() =>
            TextNormalization.Normalize(null!));
    }

    [Fact]
    public void Returns_empty_for_empty_input()
    {
        Assert.Equal(string.Empty, TextNormalization.Normalize(string.Empty));
    }

    [Fact]
    public void Converts_CRLF_to_LF()
    {
        Assert.Equal("a\nb", TextNormalization.Normalize("a\r\nb"));
    }

    [Fact]
    public void Converts_standalone_CR_to_LF()
    {
        Assert.Equal("a\nb", TextNormalization.Normalize("a\rb"));
    }

    [Fact]
    public void Leaves_LF_unchanged()
    {
        Assert.Equal("a\nb", TextNormalization.Normalize("a\nb"));
    }

    [Fact]
    public void Removes_NUL_characters()
    {
        Assert.Equal("ab", TextNormalization.Normalize("a\0b"));
    }

    [Fact]
    public void Trims_leading_whitespace()
    {
        Assert.Equal("hello", TextNormalization.Normalize("  hello"));
    }

    [Fact]
    public void Trims_trailing_whitespace()
    {
        Assert.Equal("hello", TextNormalization.Normalize("hello  "));
    }

    [Fact]
    public void Preserves_internal_blank_lines()
    {
        Assert.Equal("a\n\nb", TextNormalization.Normalize("a\n\nb"));
    }

    [Fact]
    public void Mixed_line_endings_normalized()
    {
        Assert.Equal("a\nb\nc", TextNormalization.Normalize("a\r\nb\rc"));
    }

    [Fact]
    public void Consecutive_CRs_each_become_LF()
    {
        Assert.Equal("a\n\nb", TextNormalization.Normalize("a\r\rb"));
    }

    [Fact]
    public void NUL_between_CRLFs_removed()
    {
        Assert.Equal("a\n\nb", TextNormalization.Normalize("a\r\n\0\r\nb"));
    }

    [Fact]
    public void Whitespace_only_input_becomes_empty()
    {
        Assert.Equal(string.Empty, TextNormalization.Normalize("   \n  \r\n  "));
    }

    [Fact]
    public void CR_at_end_of_input_becomes_LF_then_trimmed()
    {
        Assert.Equal("hello", TextNormalization.Normalize("hello\r"));
    }

    [Fact]
    public void CRLF_at_end_of_input_trimmed()
    {
        Assert.Equal("hello", TextNormalization.Normalize("hello\r\n"));
    }
}
