using Microsoft.Extensions.Options;

namespace OpsFlow.Infrastructure.Configuration;

/// <summary>
/// Validates <see cref="JwtOptions"/> at startup. Failure messages never include
/// the signing-key value.
/// </summary>
internal sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    /// <summary>Minimum decoded signing-key length in bytes (256-bit for HS256).</summary>
    public const int MinimumSigningKeyBytes = 32;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add("Jwt:Issuer must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add("Jwt:Audience must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            failures.Add("Jwt:SigningKey must not be empty.");
        }
        else if (!TryGetDecodedKeyLength(options.SigningKey, out var keyByteLength))
        {
            failures.Add("Jwt:SigningKey must be a valid Base64-encoded value.");
        }
        else if (keyByteLength < MinimumSigningKeyBytes)
        {
            failures.Add(
                $"Jwt:SigningKey must decode to at least {MinimumSigningKeyBytes} bytes (256-bit).");
        }

        if (options.AccessTokenLifetimeMinutes <= 0)
        {
            failures.Add("Jwt:AccessTokenLifetimeMinutes must be positive.");
        }

        if (options.RefreshTokenLifetimeDays <= 0)
        {
            failures.Add("Jwt:RefreshTokenLifetimeDays must be positive.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static bool TryGetDecodedKeyLength(string base64, out int byteLength)
    {
        var buffer = new byte[base64.Length];
        if (Convert.TryFromBase64String(base64, buffer, out byteLength))
        {
            return true;
        }

        byteLength = 0;
        return false;
    }
}
