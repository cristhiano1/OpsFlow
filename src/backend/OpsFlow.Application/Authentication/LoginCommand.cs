namespace OpsFlow.Application.Authentication;

/// <summary>
/// The input to the Application login use case. Carries the raw credentials
/// supplied by the caller and nothing technology-specific.
/// </summary>
/// <param name="Email">The email address being authenticated.</param>
/// <param name="Password">The plaintext password supplied by the caller.</param>
public sealed record LoginCommand(string Email, string Password);
