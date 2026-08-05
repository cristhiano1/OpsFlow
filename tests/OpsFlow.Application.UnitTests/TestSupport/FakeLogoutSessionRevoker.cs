using OpsFlow.Application.Authentication;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeLogoutSessionRevoker(IList<string>? invocationLog = null) : ILogoutSessionRevoker
{
    public Exception? ExceptionToThrow { get; set; }

    public int CallCount { get; private set; }

    public LogoutRevocationRequest? ReceivedRequest { get; private set; }

    public CancellationToken ReceivedCancellationToken { get; private set; }

    public Task RevokeAsync(LogoutRevocationRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        ReceivedRequest = request;
        ReceivedCancellationToken = cancellationToken;
        invocationLog?.Add("revoke");

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.CompletedTask;
    }
}
