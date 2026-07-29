namespace OpsFlow.Infrastructure.Identity;

/// <summary>Reason a refresh token was revoked. Deliberately not part of the pure domain model.</summary>
public enum RefreshTokenRevocationReason
{
    /// <summary>Superseded by a newly issued token during normal rotation.</summary>
    Rotated,

    /// <summary>Revoked because the user logged out.</summary>
    Logout,

    /// <summary>Revoked because a rotated token was presented again (token reuse).</summary>
    ReuseDetected,

    /// <summary>Revoked because the owning user was deactivated.</summary>
    UserDisabled,

    /// <summary>Revoked because of a security-sensitive change (password, roles).</summary>
    SecurityChange,
}
