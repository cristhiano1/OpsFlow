namespace OpsFlow.Application.Authentication;

/// <summary>
/// Persists the successful-login state and issues the initial refresh-token
/// session. The Infrastructure implementation will revalidate the user and
/// organization state, validate the expected security stamp, create the initial
/// refresh-token session, persist only the refresh-token hash, and atomically
/// reset the failed-access state, update the last-login timestamp, and persist
/// the refresh-token record. No transaction or persistence types are exposed
/// through this abstraction.
/// </summary>
public interface ILoginSessionIssuer
{
    /// <summary>Revalidates the authenticated state and issues a login session.</summary>
    /// <param name="request">The revalidation inputs captured at authentication time.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An issued result with the raw refresh token, or a rejected result.</returns>
    Task<SessionIssueResult> IssueAsync(
        LoginSessionRequest request,
        CancellationToken cancellationToken);
}
