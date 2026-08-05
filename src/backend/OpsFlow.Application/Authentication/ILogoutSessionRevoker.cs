namespace OpsFlow.Application.Authentication;

/// <summary>
/// Atomically closes the refresh-token family identified by the presented
/// refresh token. The infrastructure implementation performs the lookup,
/// family revocation, and persistence inside a single transaction that
/// follows the project-wide User → RefreshToken lock order.
/// <para>
/// Unknown or already-fully-revoked tokens are neutrally handled without
/// throwing; the operation is idempotent from the caller's perspective. No
/// transaction or persistence types are exposed through this abstraction.
/// </para>
/// </summary>
public interface ILogoutSessionRevoker
{
    /// <summary>Revokes the token family associated with the supplied refresh token.</summary>
    /// <param name="request">The raw refresh-token value to look up. Must not be <c>null</c>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task RevokeAsync(LogoutRevocationRequest request, CancellationToken cancellationToken);
}
