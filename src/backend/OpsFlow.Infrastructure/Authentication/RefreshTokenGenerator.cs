using System.Buffers.Text;
using System.Security.Cryptography;

namespace OpsFlow.Infrastructure.Authentication;

/// <summary>
/// Generates cryptographically secure, opaque refresh tokens (32 random bytes,
/// Base64Url-encoded). The raw token is returned to the caller and is never
/// logged or persisted (only its hash is stored elsewhere).
/// </summary>
internal sealed class RefreshTokenGenerator : IRefreshTokenGenerator
{
    /// <summary>Number of random bytes in a refresh token (256-bit).</summary>
    public const int TokenByteLength = 32;

    /// <inheritdoc />
    public string Generate()
    {
        Span<byte> bytes = stackalloc byte[TokenByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }
}
