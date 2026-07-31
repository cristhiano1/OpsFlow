using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Authentication;
using OpsFlow.Application.Authorization;
using OpsFlow.Infrastructure.Configuration;

namespace OpsFlow.Infrastructure.Authentication;

/// <summary>
/// Creates HS256 JWT access tokens with <see cref="JsonWebTokenHandler"/>. Only
/// the safe claims from <see cref="AccessTokenDescriptor"/> are emitted; no
/// password, refresh-token, hash, lockout, or concurrency information is
/// included. Token values and signing material are never logged.
/// </summary>
internal sealed class JwtAccessTokenService(IOptions<JwtOptions> options, IClock clock) : IAccessTokenService
{
    private readonly JwtOptions _options = options.Value;
    private readonly IClock _clock = clock;
    private readonly JsonWebTokenHandler _handler = new();

    /// <inheritdoc />
    public AccessTokenResult CreateAccessToken(AccessTokenDescriptor descriptor)
    {
        var issuedAt = _clock.UtcNow;
        var expiresAt = issuedAt.AddMinutes(_options.AccessTokenLifetimeMinutes);

        var claims = new List<Claim>
        {
            new(OpsFlowClaimTypes.Subject, descriptor.UserId.ToString()),
            new(OpsFlowClaimTypes.Email, descriptor.Email),
            new(OpsFlowClaimTypes.Name, descriptor.DisplayName),
            new(OpsFlowClaimTypes.OrganizationId, descriptor.OrganizationId.ToString()),
            new(OpsFlowClaimTypes.SecurityStamp, descriptor.SecurityStamp),
            new(OpsFlowClaimTypes.TokenId, Guid.NewGuid().ToString()),
        };

        foreach (var role in descriptor.Roles)
        {
            claims.Add(new Claim(OpsFlowClaimTypes.Role, role));
        }

        var signingKey = new SymmetricSecurityKey(Convert.FromBase64String(_options.SigningKey));
        var signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = signingCredentials,
        };

        var token = _handler.CreateToken(tokenDescriptor);
        return new AccessTokenResult(token, expiresAt);
    }
}
