namespace OpsFlow.Infrastructure.Authentication;

/// <summary>
/// Executes one password verification against a cached dummy hash so that the
/// authenticator's unknown-email and locked-out branches spend approximately
/// the same time as the real credential-verification path. The verification
/// result is intentionally discarded. The concrete hashing algorithm is
/// whichever <see cref="Microsoft.AspNetCore.Identity.IPasswordHasher{TUser}"/>
/// implementation Identity is configured with.
/// </summary>
internal interface IDummyPasswordVerifier
{
    /// <summary>Verifies the supplied password against the cached dummy hash and discards the result.</summary>
    /// <param name="password">The supplied password. Must not be <c>null</c>.</param>
    void Verify(string password);
}
