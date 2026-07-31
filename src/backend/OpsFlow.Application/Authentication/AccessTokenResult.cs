namespace OpsFlow.Application.Authentication;

/// <summary>The outcome of creating an access token: the token string and its exact expiration.</summary>
public sealed record AccessTokenResult(string Token, DateTimeOffset ExpiresAt);
