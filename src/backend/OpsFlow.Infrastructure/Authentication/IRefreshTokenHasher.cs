namespace OpsFlow.Infrastructure.Authentication;

/// <summary>Hashes raw refresh tokens so that only the hash is ever stored.</summary>
internal interface IRefreshTokenHasher
{
    /// <summary>Returns a deterministic hash of the raw token.</summary>
    /// <exception cref="System.ArgumentException">Thrown when the raw token is null, empty, or whitespace.</exception>
    string Hash(string rawToken);
}
