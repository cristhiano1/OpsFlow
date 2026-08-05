namespace OpsFlow.Application.Authentication;

/// <summary>
/// The outcome status of an authentication attempt. Every failure maps to the
/// same public HTTP 401 response; the distinction exists only for internal
/// server-side logging and control flow.
/// </summary>
public enum AuthenticationStatus
{
    /// <summary>The credentials were valid and all account checks passed.</summary>
    Success,

    /// <summary>The email or password was incorrect.</summary>
    InvalidCredentials,

    /// <summary>The account is currently locked out.</summary>
    LockedOut,

    /// <summary>The user account is disabled.</summary>
    UserInactive,

    /// <summary>The user's organization is disabled.</summary>
    OrganizationInactive,
}

/// <summary>
/// The result of an authentication attempt. Use the <see cref="Success"/> and
/// <see cref="Failure"/> factory methods; the private constructor prevents
/// inconsistent combinations of <see cref="Status"/> and <see cref="User"/>.
/// </summary>
public sealed record AuthenticationResult
{
    private AuthenticationResult(AuthenticationStatus status, AuthenticatedUser? user)
    {
        Status = status;
        User = user;
    }

    /// <summary>The outcome status of the authentication attempt.</summary>
    public AuthenticationStatus Status { get; }

    /// <summary>The authenticated user snapshot on success; <c>null</c> on any failure.</summary>
    public AuthenticatedUser? User { get; }

    /// <summary>Creates a successful result for the supplied authenticated user.</summary>
    /// <param name="user">The authenticated user snapshot. Must not be <c>null</c>.</param>
    public static AuthenticationResult Success(AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new AuthenticationResult(AuthenticationStatus.Success, user);
    }

    /// <summary>Creates a failed result with the supplied failure status.</summary>
    /// <param name="status">The failure status. Must not be <see cref="AuthenticationStatus.Success"/>.</param>
    public static AuthenticationResult Failure(AuthenticationStatus status)
    {
        if (status == AuthenticationStatus.Success)
        {
            throw new ArgumentException(
                "A failure result cannot use the Success status.", nameof(status));
        }

        return new AuthenticationResult(status, user: null);
    }
}
