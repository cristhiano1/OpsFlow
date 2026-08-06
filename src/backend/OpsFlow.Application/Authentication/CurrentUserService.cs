namespace OpsFlow.Application.Authentication;

/// <summary>
/// Coordinates the /auth/me use case. The HTTP layer supplies the trusted
/// query built from the presented access token; this service delegates to
/// <see cref="ICurrentUserReader"/> for the authoritative DB read. The
/// service references no HTTP, cookie, persistence, or Identity type and
/// never logs the SecurityStamp or any other credential material.
/// </summary>
public sealed class CurrentUserService
{
    private readonly ICurrentUserReader _currentUserReader;

    /// <summary>Creates the service with its collaborator.</summary>
    public CurrentUserService(ICurrentUserReader currentUserReader)
    {
        ArgumentNullException.ThrowIfNull(currentUserReader);
        _currentUserReader = currentUserReader;
    }

    /// <summary>
    /// Resolves the caller's current profile against the authoritative DB.
    /// Every check the reader performs collapses to a neutral
    /// <see cref="CurrentUserResult.Failure"/> so the HTTP layer cannot reveal
    /// which authoritative check rejected the caller.
    /// </summary>
    /// <param name="query">The query. Must not be <c>null</c>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task<CurrentUserResult> GetCurrentUserAsync(
        CurrentUserQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await _currentUserReader.ReadAsync(query, cancellationToken);
    }
}
