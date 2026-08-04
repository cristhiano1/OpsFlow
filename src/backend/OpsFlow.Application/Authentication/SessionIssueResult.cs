namespace OpsFlow.Application.Authentication;

/// <summary>
/// The outcome status of a login-session issuance attempt.
/// </summary>
public enum SessionIssueStatus
{
    /// <summary>A refresh-token session was created and persisted.</summary>
    Issued,

    /// <summary>
    /// The persisted user or organization state no longer matched the
    /// authenticated snapshot, so no session was issued.
    /// </summary>
    Rejected,
}

/// <summary>
/// The result of issuing a login session. Use the <see cref="Issued"/> and
/// <see cref="Rejected"/> factory methods; the private constructor prevents
/// inconsistent combinations.
/// <para>
/// The raw refresh token carried by an issued result is transient server-side
/// data: it must never be persisted, never be logged, and never be serialized in
/// JSON. It may later leave the server only through a <c>Set-Cookie</c> header.
/// </para>
/// </summary>
public sealed record SessionIssueResult
{
    private SessionIssueResult(
        SessionIssueStatus status,
        string? refreshToken,
        DateTimeOffset? refreshTokenExpiresAt)
    {
        Status = status;
        RefreshToken = refreshToken;
        RefreshTokenExpiresAt = refreshTokenExpiresAt;
    }

    /// <summary>The outcome status of the issuance attempt.</summary>
    public SessionIssueStatus Status { get; }

    /// <summary>The raw refresh token on success; <c>null</c> when rejected.</summary>
    public string? RefreshToken { get; }

    /// <summary>The refresh token's exact UTC expiration on success; <c>null</c> when rejected.</summary>
    public DateTimeOffset? RefreshTokenExpiresAt { get; }

    /// <summary>Creates a successful result carrying the issued refresh token.</summary>
    /// <param name="refreshToken">The raw refresh token. Must not be null, empty, or whitespace.</param>
    /// <param name="refreshTokenExpiresAt">The exact UTC instant the refresh token expires.</param>
    public static SessionIssueResult Issued(
        string refreshToken,
        DateTimeOffset refreshTokenExpiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        return new SessionIssueResult(SessionIssueStatus.Issued, refreshToken, refreshTokenExpiresAt);
    }

    /// <summary>Creates a rejected result with no token, indicating a detected state change.</summary>
    public static SessionIssueResult Rejected()
        => new(SessionIssueStatus.Rejected, refreshToken: null, refreshTokenExpiresAt: null);
}
