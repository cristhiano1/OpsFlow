namespace OpsFlow.Application.Authentication;

/// <summary>
/// The result of the /auth/me use case. Use the <see cref="Success"/> and
/// <see cref="Failure"/> factory methods; the private constructor prevents
/// inconsistent combinations.
/// <para>
/// Failure carries no reason detail on purpose: the HTTP layer must return an
/// undifferentiated 401 whether the cause is a missing user, a disabled user,
/// a locked-out user, a disabled organization, a missing organization, or a
/// SecurityStamp mismatch.
/// </para>
/// </summary>
public sealed record CurrentUserResult
{
    private CurrentUserResult(bool succeeded, LoginResultUser? user)
    {
        Succeeded = succeeded;
        User = user;
    }

    /// <summary>Whether the caller passed every authoritative DB check.</summary>
    public bool Succeeded { get; }

    /// <summary>The current, DB-authoritative user profile on success; <c>null</c> on failure.</summary>
    public LoginResultUser? User { get; }

    /// <summary>Creates a successful current-user result.</summary>
    /// <param name="user">The DB-authoritative user profile. Must not be <c>null</c>.</param>
    public static CurrentUserResult Success(LoginResultUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new CurrentUserResult(succeeded: true, user: user);
    }

    /// <summary>Creates a failed current-user result with no user.</summary>
    public static CurrentUserResult Failure()
        => new(succeeded: false, user: null);
}
