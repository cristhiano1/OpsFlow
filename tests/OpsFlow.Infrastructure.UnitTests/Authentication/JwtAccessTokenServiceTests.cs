using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpsFlow.Application.Authentication;
using OpsFlow.Infrastructure.Authentication;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.UnitTests.TestSupport;

namespace OpsFlow.Infrastructure.UnitTests.Authentication;

public sealed class JwtAccessTokenServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (JwtAccessTokenService Service, JwtOptions Options) CreateService(string? signingKey = null)
    {
        var options = new JwtOptions
        {
            Issuer = "OpsFlow",
            Audience = "OpsFlow.Api",
            SigningKey = signingKey ?? SigningKeys.NewBase64Key(32),
            AccessTokenLifetimeMinutes = 15,
            RefreshTokenLifetimeDays = 14,
        };

        var service = new JwtAccessTokenService(Options.Create(options), new FixedClock(FixedNow));
        return (service, options);
    }

    private static AccessTokenDescriptor Descriptor(params string[] roles) => new(
        UserId: Guid.NewGuid(),
        Email: "user@opsflow.local",
        DisplayName: "Test User",
        OrganizationId: Guid.NewGuid(),
        Roles: roles,
        SecurityStamp: "STAMP-VALUE");

    private static TokenValidationParameters SignatureParameters(
        string base64Key,
        string issuer = "OpsFlow",
        string audience = "OpsFlow.Api")
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = false, // lifetime is covered separately; keep signature tests deterministic
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(base64Key)),
            ValidAlgorithms = ["HS256"],
        };
    }

    [Fact]
    public async Task Token_validates_with_the_configured_signing_key()
    {
        var (service, options) = CreateService();
        var result = service.CreateAccessToken(Descriptor("Coordinator"));

        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var validation = await handler.ValidateTokenAsync(result.Token, SignatureParameters(options.SigningKey));

        Assert.True(validation.IsValid);
    }

    [Fact]
    public async Task Validation_fails_with_a_different_signing_key()
    {
        var (service, _) = CreateService();
        var result = service.CreateAccessToken(Descriptor());

        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var validation = await handler.ValidateTokenAsync(
            result.Token, SignatureParameters(SigningKeys.NewBase64Key(32)));

        Assert.False(validation.IsValid);
    }

    [Fact]
    public async Task Validation_fails_with_an_incorrect_issuer()
    {
        var (service, options) = CreateService();
        var result = service.CreateAccessToken(Descriptor());

        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var validation = await handler.ValidateTokenAsync(
            result.Token, SignatureParameters(options.SigningKey, issuer: "Wrong.Issuer"));

        Assert.False(validation.IsValid);
    }

    [Fact]
    public async Task Validation_fails_with_an_incorrect_audience()
    {
        var (service, options) = CreateService();
        var result = service.CreateAccessToken(Descriptor());

        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var validation = await handler.ValidateTokenAsync(
            result.Token, SignatureParameters(options.SigningKey, audience: "Wrong.Audience"));

        Assert.False(validation.IsValid);
    }

    [Fact]
    public void Token_has_expected_issuer_and_audience()
    {
        var (service, _) = CreateService();
        var jwt = ReadToken(service.CreateAccessToken(Descriptor()).Token);

        Assert.Equal("OpsFlow", jwt.Issuer);
        Assert.Contains("OpsFlow.Api", jwt.Audiences);
    }

    [Fact]
    public void Token_contains_the_expected_identity_claims()
    {
        var (service, _) = CreateService();
        var descriptor = Descriptor("Coordinator");
        var jwt = ReadToken(service.CreateAccessToken(descriptor).Token);

        Assert.Equal(descriptor.UserId.ToString(), jwt.GetClaim("sub").Value);
        Assert.Equal(descriptor.Email, jwt.GetClaim("email").Value);
        Assert.Equal(descriptor.DisplayName, jwt.GetClaim("name").Value);
        Assert.Equal(descriptor.OrganizationId.ToString(), jwt.GetClaim("org_id").Value);
        Assert.Equal(descriptor.SecurityStamp, jwt.GetClaim("sstamp").Value);
        Assert.False(string.IsNullOrWhiteSpace(jwt.GetClaim("jti").Value));
    }

    [Fact]
    public void Token_contains_one_role_claim_per_role()
    {
        var (service, _) = CreateService();
        var jwt = ReadToken(service.CreateAccessToken(Descriptor("Coordinator", "Technician")).Token);

        var roles = jwt.Claims.Where(c => c.Type == "role").Select(c => c.Value).ToList();
        Assert.Equal(new[] { "Coordinator", "Technician" }, roles);
    }

    [Fact]
    public void Two_tokens_receive_different_jti_values()
    {
        var (service, _) = CreateService();
        var first = ReadToken(service.CreateAccessToken(Descriptor()).Token).GetClaim("jti").Value;
        var second = ReadToken(service.CreateAccessToken(Descriptor()).Token).GetClaim("jti").Value;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Expiration_matches_the_configured_lifetime_with_a_fixed_clock()
    {
        var (service, _) = CreateService();
        var result = service.CreateAccessToken(Descriptor());

        Assert.Equal(FixedNow.AddMinutes(15), result.ExpiresAt);
    }

    [Fact]
    public void Token_does_not_contain_password_or_refresh_or_hash_claims()
    {
        var (service, _) = CreateService();
        var jwt = ReadToken(service.CreateAccessToken(Descriptor("Viewer")).Token);
        var types = jwt.Claims.Select(c => c.Type).ToList();

        Assert.DoesNotContain(types, t => t.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, t => t.Contains("refresh", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, t => t.Contains("hash", StringComparison.OrdinalIgnoreCase));
    }

    private static JsonWebToken ReadToken(string token)
        => new JsonWebTokenHandler().ReadJsonWebToken(token);
}
