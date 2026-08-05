namespace OpsFlow.Application.Authentication;

/// <summary>
/// The neutral request passed to <see cref="ILogoutSessionRevoker"/>. Carries
/// the raw refresh-token value; the revoker hashes it internally for database
/// lookup. Must never appear in logs or persistent storage.
/// </summary>
/// <param name="RawRefreshToken">The raw refresh-token value from the HttpOnly cookie.</param>
public sealed record LogoutRevocationRequest(string RawRefreshToken);
