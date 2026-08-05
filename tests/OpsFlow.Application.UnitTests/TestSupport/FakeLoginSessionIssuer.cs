using OpsFlow.Application.Authentication;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeLoginSessionIssuer(IList<string>? invocationLog = null) : ILoginSessionIssuer
{
    public SessionIssueResult? ResultToReturn { get; set; }

    public Exception? ExceptionToThrow { get; set; }

    public int CallCount { get; private set; }

    public LoginSessionRequest? ReceivedRequest { get; private set; }

    public CancellationToken ReceivedCancellationToken { get; private set; }

    public Task<SessionIssueResult> IssueAsync(
        LoginSessionRequest request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        ReceivedRequest = request;
        ReceivedCancellationToken = cancellationToken;
        invocationLog?.Add("issue");

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(
            ResultToReturn ?? throw new InvalidOperationException("ResultToReturn was not configured."));
    }
}
