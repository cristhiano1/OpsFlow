namespace OpsFlow.Application.Authentication;

/// <summary>
/// The neutral request used to revalidate the authenticated state and persist a
/// login session. Its <see cref="ExpectedSecurityStamp"/> lets the session
/// issuer detect a state change that occurred between authentication and
/// persistence. This value must remain internal to the Application and
/// Infrastructure layers and must never be copied into a public API response.
/// </summary>
/// <param name="UserId">The identifier of the user to issue a session for.</param>
/// <param name="OrganizationId">The expected organization the user belongs to.</param>
/// <param name="ExpectedSecurityStamp">The security stamp captured at authentication time.</param>
public sealed record LoginSessionRequest(
    Guid UserId,
    Guid OrganizationId,
    string ExpectedSecurityStamp);
