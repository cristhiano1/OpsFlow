using OpsFlow.Application.Authentication;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeCurrentUserReader : ICurrentUserReader
{
    public Exception? ExceptionToThrow { get; set; }

    public CurrentUserResult ResultToReturn { get; set; } = CurrentUserResult.Failure();

    public int CallCount { get; private set; }

    public CurrentUserQuery? ReceivedQuery { get; private set; }

    public CancellationToken ReceivedCancellationToken { get; private set; }

    public Task<CurrentUserResult> ReadAsync(CurrentUserQuery query, CancellationToken cancellationToken)
    {
        CallCount++;
        ReceivedQuery = query;
        ReceivedCancellationToken = cancellationToken;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(ResultToReturn);
    }
}
