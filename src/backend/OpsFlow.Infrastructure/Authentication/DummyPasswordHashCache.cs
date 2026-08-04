using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using OpsFlow.Infrastructure.Identity;

namespace OpsFlow.Infrastructure.Authentication;

/// <summary>
/// Singleton cache that holds the dummy <see cref="ApplicationUser"/> and dummy
/// password hash used by <see cref="DummyPasswordVerifier"/> to equalize timing
/// on the unknown-email and locked-out branches. The cache never retains the
/// scoped <see cref="IPasswordHasher{TUser}"/> or the throwaway plaintext used
/// to seed the hash. Initialization runs exactly once per cache instance, even
/// under concurrency.
/// </summary>
internal sealed class DummyPasswordHashCache
{
    private readonly Lock _sync = new();
    private DummyEntry? _entry;

    /// <summary>The cached dummy user and hash pair.</summary>
    /// <param name="DummyUser">A throwaway <see cref="ApplicationUser"/>. Not persisted.</param>
    /// <param name="DummyHash">A password hash produced by the configured hasher.</param>
    public sealed record DummyEntry(ApplicationUser DummyUser, string DummyHash);

    /// <summary>
    /// Returns the cached entry, initializing it exactly once using the
    /// supplied scoped hasher. The hasher is not retained beyond this call.
    /// </summary>
    /// <param name="hasher">The scoped <see cref="IPasswordHasher{TUser}"/> to seed the cache with.</param>
    public DummyEntry GetOrCreate(IPasswordHasher<ApplicationUser> hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);

        lock (_sync)
        {
            return _entry ??= CreateEntry(hasher);
        }
    }

    private static DummyEntry CreateEntry(IPasswordHasher<ApplicationUser> hasher)
    {
        var dummyUser = new ApplicationUser
        {
            Id = Guid.Empty,
            DisplayName = "dummy",
        };

        // Generate a strong throwaway plaintext, hash it, and let the
        // plaintext fall out of scope. It is never stored, logged, or
        // returned to any caller.
        var throwaway = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var dummyHash = hasher.HashPassword(dummyUser, throwaway);

        return new DummyEntry(dummyUser, dummyHash);
    }
}
