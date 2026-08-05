namespace OpsFlow.Application.Authentication;

/// <summary>
/// The application-layer command for a refresh attempt. Carries only the raw
/// refresh token extracted from the caller's cookie by the HTTP layer; it is
/// never logged or persisted.
/// </summary>
/// <param name="RawRefreshToken">The raw refresh-token value from the HttpOnly cookie.</param>
public sealed record RefreshCommand(string RawRefreshToken);
