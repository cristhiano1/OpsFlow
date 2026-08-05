using OpsFlow.Application.Authentication;

namespace OpsFlow.Api.IntegrationTests.Authentication;

internal sealed class ThrowingLogoutSessionRevoker : ILogoutSessionRevoker
{
    public Task RevokeAsync(LogoutRevocationRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated infrastructure failure.");
}
