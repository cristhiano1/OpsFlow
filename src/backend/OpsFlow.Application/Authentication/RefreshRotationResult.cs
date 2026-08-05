namespace OpsFlow.Application.Authentication;

/// <summary>The outcome status of a refresh-rotation attempt.</summary>
public enum RefreshRotationStatus
{
    /// <summary>The refresh token was successfully rotated and a fresh session was persisted.</summary>
    Rotated,

    /// <summary>
    /// The refresh token could not be rotated (unknown/expired/revoked/lost race/
    /// security-state change). No public detail is exposed.
    /// </summary>
    Rejected,
}

/// <summary>
/// The result of a refresh-token rotation. Use the <see cref="Rotated"/> and
/// <see cref="Rejected"/> factory methods; the private constructor prevents
/// inconsistent combinations.
/// <para>
/// The access token is minted inside the same transaction that persists the
/// new refresh-token row, so a returned <see cref="Rotated"/> result implies
/// that both the successor refresh-token and the newly signed access token
/// were derived from a single, transactionally validated user snapshot.
/// </para>
/// <para>
/// The raw refresh token carried by a <see cref="Rotated"/> result is
/// transient server-side data: it must never be persisted, never be logged,
/// and never be serialized in JSON. It may leave the server only through a
/// <c>Set-Cookie</c> header written by OpsFlow.Api.
/// </para>
/// </summary>
public sealed record RefreshRotationResult
{
    private RefreshRotationResult(
        RefreshRotationStatus status,
        string? accessToken,
        DateTimeOffset? accessTokenExpiresAt,
        string? newRefreshToken,
        DateTimeOffset? newRefreshTokenExpiresAt,
        LoginResultUser? user)
    {
        Status = status;
        AccessToken = accessToken;
        AccessTokenExpiresAt = accessTokenExpiresAt;
        NewRefreshToken = newRefreshToken;
        NewRefreshTokenExpiresAt = newRefreshTokenExpiresAt;
        User = user;
    }

    /// <summary>The outcome status of the rotation attempt.</summary>
    public RefreshRotationStatus Status { get; }

    /// <summary>The signed access token on success; <c>null</c> when rejected.</summary>
    public string? AccessToken { get; }

    /// <summary>The access token's exact UTC expiration on success; <c>null</c> when rejected.</summary>
    public DateTimeOffset? AccessTokenExpiresAt { get; }

    /// <summary>The raw successor refresh token on success; <c>null</c> when rejected.</summary>
    public string? NewRefreshToken { get; }

    /// <summary>The successor refresh token's exact UTC expiration on success; <c>null</c> when rejected.</summary>
    public DateTimeOffset? NewRefreshTokenExpiresAt { get; }

    /// <summary>The safe user view on success; <c>null</c> when rejected.</summary>
    public LoginResultUser? User { get; }

    /// <summary>Creates a successful rotation result.</summary>
    public static RefreshRotationResult Rotated(
        string accessToken,
        DateTimeOffset accessTokenExpiresAt,
        string newRefreshToken,
        DateTimeOffset newRefreshTokenExpiresAt,
        LoginResultUser user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(newRefreshToken);
        ArgumentNullException.ThrowIfNull(user);

        return new RefreshRotationResult(
            RefreshRotationStatus.Rotated,
            accessToken,
            accessTokenExpiresAt,
            newRefreshToken,
            newRefreshTokenExpiresAt,
            user);
    }

    /// <summary>Creates a rejected rotation result with no tokens or user.</summary>
    public static RefreshRotationResult Rejected()
        => new(
            RefreshRotationStatus.Rejected,
            accessToken: null,
            accessTokenExpiresAt: null,
            newRefreshToken: null,
            newRefreshTokenExpiresAt: null,
            user: null);
}
