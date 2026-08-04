using Microsoft.AspNetCore.Identity;
using OpsFlow.Infrastructure.Identity;

namespace OpsFlow.Infrastructure.Authentication;

/// <summary>
/// Runs one password verification against a shared cached dummy hash to
/// equalize timing on the unknown-email and locked-out branches. The verifier
/// is scoped so it can safely resolve the scoped
/// <see cref="IPasswordHasher{TUser}"/>; the cache holding the dummy hash is
/// a singleton (<see cref="DummyPasswordHashCache"/>).
/// </summary>
internal sealed class DummyPasswordVerifier : IDummyPasswordVerifier
{
    private readonly IPasswordHasher<ApplicationUser> _passwordHasher;
    private readonly DummyPasswordHashCache _cache;

    public DummyPasswordVerifier(
        IPasswordHasher<ApplicationUser> passwordHasher,
        DummyPasswordHashCache cache)
    {
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(cache);

        _passwordHasher = passwordHasher;
        _cache = cache;
    }

    public void Verify(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var entry = _cache.GetOrCreate(_passwordHasher);

        // The result is intentionally discarded: this call exists only to
        // spend roughly the same time as a real verification.
        _ = _passwordHasher.VerifyHashedPassword(entry.DummyUser, entry.DummyHash, password);
    }
}
