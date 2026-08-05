namespace OpsFlow.Application.Authentication;

/// <summary>
/// Coordinates the refresh use case. It delegates the atomic rotation
/// (validation, family/reuse handling, security-state binding, access-token
/// minting, and successor persistence) to <see cref="IRefreshSessionRotator"/>
/// and shapes the outcome into a neutral <see cref="RefreshResult"/>. The
/// service references no HTTP, cookie, persistence, or Identity type and
/// never logs credential or token values.
/// </summary>
public sealed class RefreshService
{
    private readonly IRefreshSessionRotator _refreshSessionRotator;

    /// <summary>Creates the service with its collaborator.</summary>
    /// <param name="refreshSessionRotator">Atomically rotates the refresh session.</param>
    public RefreshService(IRefreshSessionRotator refreshSessionRotator)
    {
        ArgumentNullException.ThrowIfNull(refreshSessionRotator);
        _refreshSessionRotator = refreshSessionRotator;
    }

    /// <summary>
    /// Attempts to refresh the caller's session by rotating the supplied
    /// refresh token. Any expected failure mode returns the same neutral
    /// <see cref="RefreshResult.Failure"/> result. Infrastructure failures
    /// propagate as exceptions.
    /// </summary>
    /// <param name="command">The refresh command. Must not be <c>null</c>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task<RefreshResult> RefreshAsync(
        RefreshCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.RawRefreshToken))
        {
            return RefreshResult.Failure();
        }

        var rotation = await _refreshSessionRotator.RotateAsync(
            new RefreshRotationRequest(command.RawRefreshToken),
            cancellationToken);

        if (rotation.Status != RefreshRotationStatus.Rotated
            || rotation.AccessToken is null
            || rotation.AccessTokenExpiresAt is null
            || rotation.NewRefreshToken is null
            || rotation.NewRefreshTokenExpiresAt is null
            || rotation.User is null)
        {
            return RefreshResult.Failure();
        }

        return RefreshResult.Success(
            rotation.AccessToken,
            rotation.AccessTokenExpiresAt.Value,
            rotation.NewRefreshToken,
            rotation.NewRefreshTokenExpiresAt.Value,
            rotation.User);
    }
}
