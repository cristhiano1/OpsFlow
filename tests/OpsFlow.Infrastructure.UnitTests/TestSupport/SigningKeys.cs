using System.Security.Cryptography;

namespace OpsFlow.Infrastructure.UnitTests.TestSupport;

/// <summary>
/// Generates throwaway Base64 signing keys for tests. Keys are created at
/// runtime so no secret-like value is ever committed to the repository.
/// </summary>
internal static class SigningKeys
{
    /// <summary>Creates a random Base64-encoded key of the given byte length (default 32).</summary>
    public static string NewBase64Key(int byteLength = 32)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteLength));
}
