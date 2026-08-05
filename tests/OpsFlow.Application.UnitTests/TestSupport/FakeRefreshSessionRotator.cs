using OpsFlow.Application.Authentication;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeRefreshSessionRotator(IList<string>? invocationLog = null) : IRefreshSessionRotator
{
    public RefreshRotationResult? ResultToReturn { get; set; }

    public Exception? ExceptionToThrow { get; set; }

    public int CallCount { get; private set; }

    public RefreshRotationRequest? ReceivedRequest { get; private set; }

    public CancellationToken ReceivedCancellationToken { get; private set; }

    public Task<RefreshRotationResult> RotateAsync(
        RefreshRotationRequest request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        ReceivedRequest = request;
        ReceivedCancellationToken = cancellationToken;
        invocationLog?.Add("rotate");

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(
            ResultToReturn ?? throw new InvalidOperationException("ResultToReturn was not configured."));
    }
}
