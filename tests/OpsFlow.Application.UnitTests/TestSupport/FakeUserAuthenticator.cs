using OpsFlow.Application.Authentication;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeUserAuthenticator(IList<string>? invocationLog = null) : IUserAuthenticator
{
    public AuthenticationResult? ResultToReturn { get; set; }

    public Exception? ExceptionToThrow { get; set; }

    public int CallCount { get; private set; }

    public string? ReceivedEmail { get; private set; }

    public string? ReceivedPassword { get; private set; }

    public CancellationToken ReceivedCancellationToken { get; private set; }

    public Task<AuthenticationResult> AuthenticateAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        CallCount++;
        ReceivedEmail = email;
        ReceivedPassword = password;
        ReceivedCancellationToken = cancellationToken;
        invocationLog?.Add("authenticate");

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(
            ResultToReturn ?? throw new InvalidOperationException("ResultToReturn was not configured."));
    }
}
