namespace OpsFlow.Api.Authentication;

/// <summary>
/// Central definition of the refresh-token HttpOnly cookie used by the login
/// and refresh endpoints. Keeping the name and the <see cref="CookieOptions"/>
/// in one place guarantees that both endpoints produce a byte-identical cookie
/// contract, so a successor cookie written by refresh replaces the login
/// cookie cleanly in the browser.
/// </summary>
internal static class RefreshTokenCookie
{
    /// <summary>The cookie name.</summary>
    public const string Name = "opsflow_refresh_token";

    /// <summary>
    /// Builds the <see cref="CookieOptions"/> for a refresh-token cookie whose
    /// value expires at <paramref name="expiresAt"/>.
    /// </summary>
    public static CookieOptions BuildOptions(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/api/v1/auth",
        Expires = expiresAt,
    };

    /// <summary>
    /// Reads the refresh-token value from the incoming request cookies.
    /// Returns <c>null</c> when the cookie is missing or blank.
    /// </summary>
    public static string? ReadFrom(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var value = request.Cookies[Name];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Emits a <c>Set-Cookie</c> deletion header for the refresh-token cookie
    /// using the same name, path, and security attributes as issuance. The
    /// browser removes the cookie only if the deletion attributes match the
    /// issued cookie's attributes.
    /// </summary>
    public static void DeleteFrom(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Delete(Name, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/v1/auth",
        });
    }
}
