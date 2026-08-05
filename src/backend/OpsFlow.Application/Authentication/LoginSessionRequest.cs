namespace OpsFlow.Application.Authentication;

/// <summary>
/// The neutral request used to revalidate the authenticated state and persist a
/// login session. Its <see cref="ExpectedSecurityStamp"/> and
/// <see cref="ExpectedConcurrencyStamp"/> let the session issuer detect a state
/// change that occurred between authentication and persistence. Both values
/// must remain internal to the Application and Infrastructure layers and must
/// never be copied into a public API response.
/// </summary>
/// <param name="UserId">The identifier of the user to issue a session for.</param>
/// <param name="OrganizationId">The expected organization the user belongs to.</param>
/// <param name="ExpectedSecurityStamp">The security stamp captured at authentication time.</param>
/// <param name="ExpectedConcurrencyStamp">
/// The user's Identity concurrency stamp captured at authentication time. Any
/// Identity operation that flows through <c>UserStore.UpdateAsync</c> (role
/// change via <c>UserManager</c>, password change, lockout mutation, etc.)
/// rotates this value, so a mismatch during transactional revalidation
/// indicates the authenticated snapshot is stale and the session must not be
/// issued.
/// </param>
public sealed record LoginSessionRequest(
    Guid UserId,
    Guid OrganizationId,
    string ExpectedSecurityStamp,
    string ExpectedConcurrencyStamp);
