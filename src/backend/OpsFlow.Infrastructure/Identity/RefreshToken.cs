namespace OpsFlow.Infrastructure.Identity;

/// <summary>
/// Persistence model for a rotating refresh token. Only a SHA-256 hash of the
/// cryptographically random raw token is stored; the raw token is never
/// persisted. This type is internal to the infrastructure layer.
/// </summary>
public class RefreshToken
{
    /// <summary>Database identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning user identifier.</summary>
    public Guid UserId { get; set; }

    /// <summary>Owning user (navigation).</summary>
    public ApplicationUser? User { get; set; }

    /// <summary>SHA-256 hash (Base64) of the raw refresh token. Unique.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Identifier of the rotation family this token belongs to.</summary>
    public Guid TokenFamilyId { get; set; }

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Expiration timestamp (UTC).</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Revocation timestamp (UTC), when the token has been revoked.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Identifier of the token that replaced this one during rotation, when applicable.</summary>
    public Guid? ReplacedByTokenId { get; set; }

    /// <summary>Reason the token was revoked, when applicable.</summary>
    public RefreshTokenRevocationReason? ReasonRevoked { get; set; }

    /// <summary>SQL Server rowversion used as an optimistic-concurrency token for atomic rotation.</summary>
    public byte[] RowVersion { get; set; } = [];
}
