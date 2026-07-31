using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.UnitTests.TestSupport;

namespace OpsFlow.Infrastructure.UnitTests.Configuration;

public sealed class JwtOptionsValidatorTests
{
    private static JwtOptions ValidOptions() => new()
    {
        Issuer = "OpsFlow",
        Audience = "OpsFlow.Api",
        SigningKey = SigningKeys.NewBase64Key(32),
        AccessTokenLifetimeMinutes = 15,
        RefreshTokenLifetimeDays = 14,
    };

    private static bool IsValid(JwtOptions options)
        => new JwtOptionsValidator().Validate(name: null, options).Succeeded;

    [Fact]
    public void Valid_options_are_accepted()
        => Assert.True(IsValid(ValidOptions()));

    [Fact]
    public void Missing_issuer_is_rejected()
    {
        var options = ValidOptions();
        options.Issuer = "  ";
        Assert.False(IsValid(options));
    }

    [Fact]
    public void Missing_audience_is_rejected()
    {
        var options = ValidOptions();
        options.Audience = "";
        Assert.False(IsValid(options));
    }

    [Fact]
    public void Missing_signing_key_is_rejected()
    {
        var options = ValidOptions();
        options.SigningKey = "";
        Assert.False(IsValid(options));
    }

    [Fact]
    public void Non_base64_signing_key_is_rejected()
    {
        var options = ValidOptions();
        options.SigningKey = "this is not base64 !!!";
        Assert.False(IsValid(options));
    }

    [Fact]
    public void Signing_key_shorter_than_32_bytes_is_rejected()
    {
        var options = ValidOptions();
        options.SigningKey = SigningKeys.NewBase64Key(16);
        Assert.False(IsValid(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_access_token_lifetime_is_rejected(int minutes)
    {
        var options = ValidOptions();
        options.AccessTokenLifetimeMinutes = minutes;
        Assert.False(IsValid(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_refresh_token_lifetime_is_rejected(int days)
    {
        var options = ValidOptions();
        options.RefreshTokenLifetimeDays = days;
        Assert.False(IsValid(options));
    }

    [Fact]
    public void Failure_message_does_not_contain_the_signing_key_value()
    {
        var options = ValidOptions();
        options.SigningKey = SigningKeys.NewBase64Key(16); // valid Base64 but too short
        var result = new JwtOptionsValidator().Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.DoesNotContain(options.SigningKey, result.FailureMessage, StringComparison.Ordinal);
    }
}
