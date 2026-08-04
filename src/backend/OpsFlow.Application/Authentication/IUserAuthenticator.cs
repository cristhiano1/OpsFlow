namespace OpsFlow.Application.Authentication;

/// <summary>
/// Verifies a caller's credentials and account state and returns a neutral
/// authenticated-user snapshot. The implementation validates the password,
/// lockout state, user activity, organization activity, roles, and security
/// stamp. It performs no application-level successful-login writes; persisting a
/// login session is the responsibility of <see cref="ILoginSessionIssuer"/>.
/// </summary>
public interface IUserAuthenticator
{
    /// <summary>Authenticates the supplied credentials and returns the outcome.</summary>
    /// <param name="email">The email address being authenticated.</param>
    /// <param name="password">The plaintext password supplied by the caller.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A successful result with the user snapshot, or a failure result.</returns>
    Task<AuthenticationResult> AuthenticateAsync(
        string email,
        string password,
        CancellationToken cancellationToken);
}
