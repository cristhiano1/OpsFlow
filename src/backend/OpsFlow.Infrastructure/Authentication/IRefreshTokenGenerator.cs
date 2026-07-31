namespace OpsFlow.Infrastructure.Authentication;

/// <summary>Generates cryptographically secure, opaque refresh tokens.</summary>
internal interface IRefreshTokenGenerator
{
    /// <summary>Creates a new opaque refresh token (Base64Url-encoded random bytes).</summary>
    string Generate();
}
