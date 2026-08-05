namespace OpsFlow.Application.Authentication;

/// <summary>
/// Atomically rotates a refresh-token session. The infrastructure implementation
/// performs all validation, family/reuse handling, user- and organization-state
/// revalidation, SecurityStamp-binding verification, access-token minting, and
/// new refresh-token persistence inside a single transaction with the
/// project-wide User → RefreshToken lock order. No transaction or persistence
/// types are exposed through this abstraction.
/// </summary>
public interface IRefreshSessionRotator
{
    /// <summary>Rotates the refresh token identified by the supplied hash.</summary>
    /// <param name="request">The refresh-token hash plus the pre-transaction observation used to disambiguate a concurrency race from a replay.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<RefreshRotationResult> RotateAsync(
        RefreshRotationRequest request,
        CancellationToken cancellationToken);
}
