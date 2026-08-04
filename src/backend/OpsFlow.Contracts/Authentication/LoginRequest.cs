namespace OpsFlow.Contracts.Authentication;

/// <summary>
/// The public request body for a login attempt. Carries only the raw
/// credentials supplied by the caller; it is never logged or persisted.
/// </summary>
/// <param name="Email">The email address the caller is authenticating with.</param>
/// <param name="Password">The plaintext password supplied by the caller.</param>
public sealed record LoginRequest(string Email, string Password);
