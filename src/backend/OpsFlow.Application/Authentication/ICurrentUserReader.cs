namespace OpsFlow.Application.Authentication;

/// <summary>
/// Authoritative DB read for the /auth/me use case. The Infrastructure
/// implementation resolves the caller's current profile from the database,
/// applies every activation/lockout/organization/SecurityStamp check, and
/// exposes no persistence or Identity types through this abstraction. The
/// implementation performs no writes.
/// </summary>
public interface ICurrentUserReader
{
    /// <summary>Reads the caller's current profile and applies every authoritative check.</summary>
    /// <param name="query">The trusted inputs taken from the presented access token.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A successful result on all checks passing; a neutral failure otherwise.</returns>
    Task<CurrentUserResult> ReadAsync(CurrentUserQuery query, CancellationToken cancellationToken);
}
