using System.Security.Cryptography;
using System.Text;

namespace OpsFlow.Infrastructure.Authentication;

/// <summary>
/// Hashes raw refresh tokens with SHA-256 (Base64 output). Only the hash is ever
/// stored; the raw token is never persisted or logged.
/// </summary>
internal sealed class RefreshTokenHasher : IRefreshTokenHasher
{
    /// <inheritdoc />
    public string Hash(string rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            throw new ArgumentException("Raw token must not be null, empty, or whitespace.", nameof(rawToken));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToBase64String(hash);
    }
}
