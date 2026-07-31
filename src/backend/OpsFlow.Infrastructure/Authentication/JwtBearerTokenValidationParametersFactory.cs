using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpsFlow.Application.Authorization;
using OpsFlow.Infrastructure.Configuration;

namespace OpsFlow.Infrastructure.Authentication;

/// <summary>
/// Builds the <see cref="TokenValidationParameters"/> used to validate OpsFlow
/// access tokens. This is the single source of truth for signing key, issuer,
/// audience, lifetime, clock skew, and claim-type configuration; the API
/// middleware consumes it instead of duplicating that logic.
/// </summary>
public sealed class JwtBearerTokenValidationParametersFactory(IOptions<JwtOptions> options)
{
    /// <summary>Fixed clock skew applied to token-lifetime validation.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private readonly JwtOptions _options = options.Value;

    /// <summary>Creates a fresh set of validation parameters.</summary>
    public TokenValidationParameters Create()
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(_options.SigningKey)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = ClockSkew,
            RoleClaimType = OpsFlowClaimTypes.Role,
            NameClaimType = OpsFlowClaimTypes.Subject,
        };
    }
}
