using System.Security.Cryptography;
using System.Text;
using OpsFlow.Infrastructure.Authentication;

namespace OpsFlow.Infrastructure.UnitTests.Authentication;

public sealed class RefreshTokenHasherTests
{
    private const string KnownInput = "opsflow-refresh-token-sample";

    [Fact]
    public void Same_raw_token_produces_the_same_hash()
    {
        var hasher = new RefreshTokenHasher();

        Assert.Equal(hasher.Hash(KnownInput), hasher.Hash(KnownInput));
    }

    [Fact]
    public void Different_raw_tokens_produce_different_hashes()
    {
        var hasher = new RefreshTokenHasher();

        Assert.NotEqual(hasher.Hash("token-a"), hasher.Hash("token-b"));
    }

    [Fact]
    public void Output_equals_sha256_base64_of_the_utf8_input()
    {
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(KnownInput)));

        Assert.Equal(expected, new RefreshTokenHasher().Hash(KnownInput));
    }

    [Fact]
    public void Output_does_not_equal_the_raw_token()
    {
        Assert.NotEqual(KnownInput, new RefreshTokenHasher().Hash(KnownInput));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_empty_or_whitespace_input_is_rejected(string? rawToken)
    {
        var hasher = new RefreshTokenHasher();

        Assert.Throws<ArgumentException>(() => hasher.Hash(rawToken!));
    }
}
