namespace OpsFlow.Application.Authentication;

/// <summary>
/// The result of the Application login use case. Use the <see cref="Success"/>
/// and <see cref="Failure"/> factory methods; the private constructor prevents
/// inconsistent combinations.
/// <para>
/// This type must not be serialized directly as the public API response.
/// OpsFlow.Api maps it to a <c>LoginResponse</c>: the refresh token is removed
/// from the JSON mapping and written only to an HttpOnly cookie. The security
/// stamp intentionally never appears on this type.
/// </para>
/// </summary>
public sealed record LoginResult
{
    private LoginResult(
        bool succeeded,
        string? accessToken,
        DateTimeOffset? accessTokenExpiresAt,
        string? refreshToken,
        DateTimeOffset? refreshTokenExpiresAt,
        LoginResultUser? user)
    {
        Succeeded = succeeded;
        AccessToken = accessToken;
        AccessTokenExpiresAt = accessTokenExpiresAt;
        RefreshToken = refreshToken;
        RefreshTokenExpiresAt = refreshTokenExpiresAt;
        User = user;
    }

    /// <summary>Whether the login attempt succeeded.</summary>
    public bool Succeeded { get; }

    /// <summary>The signed access token on success; <c>null</c> on failure.</summary>
    public string? AccessToken { get; }

    /// <summary>The access token's exact UTC expiration on success; <c>null</c> on failure.</summary>
    public DateTimeOffset? AccessTokenExpiresAt { get; }

    /// <summary>
    /// The raw refresh token on success; <c>null</c> on failure. Transient
    /// server-side data: never persisted, logged, or serialized in JSON, and
    /// written only to an HttpOnly cookie by OpsFlow.Api.
    /// </summary>
    public string? RefreshToken { get; }

    /// <summary>The refresh token's exact UTC expiration on success; <c>null</c> on failure.</summary>
    public DateTimeOffset? RefreshTokenExpiresAt { get; }

    /// <summary>The safe user view on success; <c>null</c> on failure.</summary>
    public LoginResultUser? User { get; }

    /// <summary>Creates a successful login result.</summary>
    /// <param name="accessToken">The signed access token. Must not be null, empty, or whitespace.</param>
    /// <param name="accessTokenExpiresAt">The exact UTC instant the access token expires.</param>
    /// <param name="refreshToken">The raw refresh token. Must not be null, empty, or whitespace.</param>
    /// <param name="refreshTokenExpiresAt">The exact UTC instant the refresh token expires.</param>
    /// <param name="user">The safe user view. Must not be <c>null</c>.</param>
    public static LoginResult Success(
        string accessToken,
        DateTimeOffset accessTokenExpiresAt,
        string refreshToken,
        DateTimeOffset refreshTokenExpiresAt,
        LoginResultUser user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        ArgumentNullException.ThrowIfNull(user);

        return new LoginResult(
            succeeded: true,
            accessToken: accessToken,
            accessTokenExpiresAt: accessTokenExpiresAt,
            refreshToken: refreshToken,
            refreshTokenExpiresAt: refreshTokenExpiresAt,
            user: user);
    }

    /// <summary>Creates a failed login result with no tokens, expirations, or user.</summary>
    public static LoginResult Failure()
        => new(
            succeeded: false,
            accessToken: null,
            accessTokenExpiresAt: null,
            refreshToken: null,
            refreshTokenExpiresAt: null,
            user: null);
}
