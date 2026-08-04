using System.Reflection;
using OpsFlow.Infrastructure.Authentication;
using OpsFlow.Infrastructure.UnitTests.TestSupport;

namespace OpsFlow.Infrastructure.UnitTests.Authentication;

public sealed class DummyPasswordVerifierTests
{
    [Fact]
    public void Constructor_rejects_null_password_hasher()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new DummyPasswordVerifier(null!, new DummyPasswordHashCache()));

        Assert.Equal("passwordHasher", exception.ParamName);
    }

    [Fact]
    public void Constructor_rejects_null_cache()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new DummyPasswordVerifier(new CountingPasswordHasher(), null!));

        Assert.Equal("cache", exception.ParamName);
    }

    [Fact]
    public void Verify_rejects_null_password()
    {
        var verifier = new DummyPasswordVerifier(new CountingPasswordHasher(), new DummyPasswordHashCache());

        var exception = Assert.Throws<ArgumentNullException>(() => verifier.Verify(null!));

        Assert.Equal("password", exception.ParamName);
    }

    [Fact]
    public void First_verify_call_uses_the_configured_hasher_to_hash_and_to_verify()
    {
        var hasher = new CountingPasswordHasher();
        var cache = new DummyPasswordHashCache();
        var verifier = new DummyPasswordVerifier(hasher, cache);

        verifier.Verify("some-attempted-password");

        Assert.Equal(1, hasher.HashCallCount);
        Assert.Equal(1, hasher.VerifyCallCount);
        Assert.NotNull(hasher.LastHashUser);
        Assert.NotNull(hasher.LastVerifyUser);
        Assert.False(string.IsNullOrWhiteSpace(hasher.LastVerifyHashedPassword));
    }

    [Fact]
    public void Repeated_verify_calls_reuse_the_same_cached_hash()
    {
        var hasher = new CountingPasswordHasher();
        var cache = new DummyPasswordHashCache();
        var verifier = new DummyPasswordVerifier(hasher, cache);

        verifier.Verify("attempt-one");
        var hashAfterFirstCall = hasher.LastVerifyHashedPassword;

        verifier.Verify("attempt-two");
        var hashAfterSecondCall = hasher.LastVerifyHashedPassword;

        verifier.Verify("attempt-three");
        var hashAfterThirdCall = hasher.LastVerifyHashedPassword;

        Assert.Equal(1, hasher.HashCallCount);
        Assert.Equal(3, hasher.VerifyCallCount);
        Assert.NotNull(hashAfterFirstCall);
        Assert.Equal(hashAfterFirstCall, hashAfterSecondCall);
        Assert.Equal(hashAfterFirstCall, hashAfterThirdCall);
    }

    [Fact]
    public void Multiple_verifier_instances_sharing_one_cache_hash_only_once()
    {
        var hasher = new CountingPasswordHasher();
        var cache = new DummyPasswordHashCache();
        var first = new DummyPasswordVerifier(hasher, cache);
        var second = new DummyPasswordVerifier(hasher, cache);
        var third = new DummyPasswordVerifier(hasher, cache);

        first.Verify("attempt-one");
        second.Verify("attempt-two");
        third.Verify("attempt-three");

        Assert.Equal(1, hasher.HashCallCount);
        Assert.Equal(3, hasher.VerifyCallCount);
    }

    [Fact]
    public void Verify_invokes_verify_hashed_password_for_every_call()
    {
        var hasher = new CountingPasswordHasher();
        var cache = new DummyPasswordHashCache();
        var verifier = new DummyPasswordVerifier(hasher, cache);

        verifier.Verify("wrong-password-one");
        verifier.Verify("wrong-password-two");
        verifier.Verify("wrong-password-three");
        verifier.Verify("wrong-password-four");

        Assert.Equal(4, hasher.VerifyCallCount);
    }

    [Fact]
    public void Verifier_exposes_no_public_readable_state()
    {
        // Structurally confirm the verifier does not surface the cached hash
        // or throwaway plaintext through a public property.
        var readableProperties = typeof(DummyPasswordVerifier)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead)
            .ToArray();

        Assert.Empty(readableProperties);
    }
}
