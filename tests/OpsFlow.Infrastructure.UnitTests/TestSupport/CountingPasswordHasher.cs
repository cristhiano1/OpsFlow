using Microsoft.AspNetCore.Identity;
using OpsFlow.Infrastructure.Identity;

namespace OpsFlow.Infrastructure.UnitTests.TestSupport;

/// <summary>
/// A test-double <see cref="IPasswordHasher{TUser}"/> that delegates to a real
/// <see cref="PasswordHasher{TUser}"/> so hashing and verification behave
/// correctly, while counting invocations so tests can verify caching and
/// call-order expectations.
/// </summary>
internal sealed class CountingPasswordHasher : IPasswordHasher<ApplicationUser>
{
    private readonly PasswordHasher<ApplicationUser> _inner = new();

    public int HashCallCount { get; private set; }

    public int VerifyCallCount { get; private set; }

    public ApplicationUser? LastHashUser { get; private set; }

    public ApplicationUser? LastVerifyUser { get; private set; }

    public string? LastVerifyHashedPassword { get; private set; }

    public string HashPassword(ApplicationUser user, string password)
    {
        HashCallCount++;
        LastHashUser = user;
        return _inner.HashPassword(user, password);
    }

    public PasswordVerificationResult VerifyHashedPassword(
        ApplicationUser user, string hashedPassword, string providedPassword)
    {
        VerifyCallCount++;
        LastVerifyUser = user;
        LastVerifyHashedPassword = hashedPassword;
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}
