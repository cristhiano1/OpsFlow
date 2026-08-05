namespace OpsFlow.Application.Authentication;

/// <summary>
/// Coordinates the logout use case. A blank/null refresh token is a no-op
/// (idempotent success). A non-blank value is forwarded to
/// <see cref="ILogoutSessionRevoker"/> for atomic family revocation.
/// The service references no HTTP, cookie, persistence, or Identity type and
/// never logs credential or token values.
/// </summary>
public sealed class LogoutService
{
    private readonly ILogoutSessionRevoker _logoutSessionRevoker;

    /// <summary>Creates the service with its collaborator.</summary>
    public LogoutService(ILogoutSessionRevoker logoutSessionRevoker)
    {
        ArgumentNullException.ThrowIfNull(logoutSessionRevoker);
        _logoutSessionRevoker = logoutSessionRevoker;
    }

    /// <summary>
    /// Attempts to close the caller's refresh-token session. A missing raw
    /// token is treated as an idempotent no-op success. Any infrastructure
    /// exception propagates so that the caller (typically the HTTP endpoint)
    /// does NOT clear the browser cookie for genuine server failures.
    /// </summary>
    /// <param name="command">The logout command. Must not be <c>null</c>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task LogoutAsync(LogoutCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.RawRefreshToken))
        {
            return;
        }

        await _logoutSessionRevoker.RevokeAsync(
            new LogoutRevocationRequest(command.RawRefreshToken),
            cancellationToken);
    }
}
