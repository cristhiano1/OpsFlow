using Microsoft.Extensions.Options;
using OpsFlow.Infrastructure.Authentication;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.UnitTests.TestSupport;

namespace OpsFlow.Infrastructure.UnitTests.Authentication;

public sealed class JwtBearerTokenValidationParametersFactoryTests
{
    private static JwtBearerTokenValidationParametersFactory CreateFactory()
    {
        var options = new JwtOptions
        {
            Issuer = "OpsFlow",
            Audience = "OpsFlow.Api",
            SigningKey = SigningKeys.NewBase64Key(32),
            AccessTokenLifetimeMinutes = 15,
            RefreshTokenLifetimeDays = 14,
        };

        return new JwtBearerTokenValidationParametersFactory(Options.Create(options));
    }

    [Fact]
    public void Clock_skew_is_exactly_30_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), JwtBearerTokenValidationParametersFactory.ClockSkew);
        Assert.Equal(TimeSpan.FromSeconds(30), CreateFactory().Create().ClockSkew);
    }

    [Fact]
    public void All_validations_are_enabled()
    {
        var parameters = CreateFactory().Create();

        Assert.True(parameters.ValidateIssuer);
        Assert.True(parameters.ValidateAudience);
        Assert.True(parameters.ValidateLifetime);
        Assert.True(parameters.ValidateIssuerSigningKey);
        Assert.True(parameters.RequireExpirationTime);
        Assert.True(parameters.RequireSignedTokens);
    }

    [Fact]
    public void Claim_types_are_role_and_sub()
    {
        var parameters = CreateFactory().Create();

        Assert.Equal("role", parameters.RoleClaimType);
        Assert.Equal("sub", parameters.NameClaimType);
    }

    [Fact]
    public void Issuer_and_audience_come_from_options()
    {
        var parameters = CreateFactory().Create();

        Assert.Equal("OpsFlow", parameters.ValidIssuer);
        Assert.Equal("OpsFlow.Api", parameters.ValidAudience);
    }
}
