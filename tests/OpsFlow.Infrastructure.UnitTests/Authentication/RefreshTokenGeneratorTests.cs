using System.Buffers.Text;
using OpsFlow.Infrastructure.Authentication;

namespace OpsFlow.Infrastructure.UnitTests.Authentication;

public sealed class RefreshTokenGeneratorTests
{
    [Fact]
    public void Generated_token_decodes_to_exactly_32_bytes()
    {
        var token = new RefreshTokenGenerator().Generate();
        var decoded = Base64Url.DecodeFromChars(token);

        Assert.Equal(32, decoded.Length);
    }

    [Fact]
    public void Generated_token_is_base64url_safe()
    {
        var token = new RefreshTokenGenerator().Generate();

        Assert.False(token.Contains('+', StringComparison.Ordinal));
        Assert.False(token.Contains('/', StringComparison.Ordinal));
        Assert.False(token.Contains('=', StringComparison.Ordinal));
    }

    [Fact]
    public void Two_generated_tokens_differ()
    {
        var generator = new RefreshTokenGenerator();

        Assert.NotEqual(generator.Generate(), generator.Generate());
    }
}
