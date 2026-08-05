namespace OpsFlow.Application.Authentication;

/// <summary>
/// The application-layer command for a logout attempt. Carries the raw
/// refresh-token value the HTTP layer extracted from the caller's cookie, or
/// <c>null</c> when no cookie was present. The raw value is never logged or
/// persisted.
/// </summary>
/// <param name="RawRefreshToken">
/// The raw refresh-token value from the HttpOnly cookie. <c>null</c> when the
/// caller sent no refresh cookie.
/// </param>
public sealed record LogoutCommand(string? RawRefreshToken);
