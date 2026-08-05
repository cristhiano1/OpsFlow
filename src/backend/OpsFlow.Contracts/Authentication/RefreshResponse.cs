namespace OpsFlow.Contracts.Authentication;

/// <summary>
/// The public response returned on a successful refresh. It exposes the new
/// access token and its expiration together with a safe view of the
/// authenticated user. The refresh token is intentionally absent: the freshly
/// rotated refresh token never travels in the response body and leaves the
/// server only through a <c>Set-Cookie</c> header.
/// </summary>
/// <param name="AccessToken">The signed access token to send on subsequent requests.</param>
/// <param name="AccessTokenExpiresAt">The exact UTC instant at which the access token expires.</param>
/// <param name="User">A safe, non-sensitive view of the authenticated user.</param>
public sealed record RefreshResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    LoginUserResponse User);
