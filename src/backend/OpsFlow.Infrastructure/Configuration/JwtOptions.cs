namespace OpsFlow.Infrastructure.Configuration;

/// <summary>
/// JWT configuration, bound from the "Jwt" configuration section. The signing
/// key is supplied through user secrets or environment variables and is never
/// committed to a settings file.
/// </summary>
public sealed class JwtOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Jwt";

    /// <summary>Token issuer.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Token audience.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded HMAC signing key. Once decoded it must contain at least
    /// 32 bytes (256-bit) for HS256. Supplied via user secrets / environment.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Access-token lifetime in minutes (must be positive).</summary>
    public int AccessTokenLifetimeMinutes { get; set; }

    /// <summary>Refresh-token lifetime in days (must be positive).</summary>
    public int RefreshTokenLifetimeDays { get; set; }
}
