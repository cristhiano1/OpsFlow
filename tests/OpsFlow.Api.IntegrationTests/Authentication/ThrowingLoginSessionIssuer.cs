using OpsFlow.Application.Authentication;

namespace OpsFlow.Api.IntegrationTests.Authentication;

internal sealed class ThrowingLoginSessionIssuer : ILoginSessionIssuer
{
    public Task<SessionIssueResult> IssueAsync(
        LoginSessionRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated infrastructure failure.");
}
