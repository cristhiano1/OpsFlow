namespace OpsFlow.Application.Authentication;

/// <summary>
/// The neutral request passed to <see cref="IRefreshSessionRotator"/>. Carries
/// the raw refresh-token value received from the caller's cookie. The raw
/// value is transient server-side data: it must never be persisted or logged.
/// The rotator hashes it internally for database lookup.
/// </summary>
/// <param name="RawRefreshToken">The raw refresh-token value from the HttpOnly cookie.</param>
public sealed record RefreshRotationRequest(string RawRefreshToken);
