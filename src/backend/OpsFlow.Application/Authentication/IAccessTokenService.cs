namespace OpsFlow.Application.Authentication;

/// <summary>
/// Creates signed access tokens. This is a technology-neutral abstraction: no
/// JWT, IdentityModel, or persistence types leak through it.
/// </summary>
public interface IAccessTokenService
{
    /// <summary>Creates a signed access token for the supplied user descriptor.</summary>
    AccessTokenResult CreateAccessToken(AccessTokenDescriptor descriptor);
}
