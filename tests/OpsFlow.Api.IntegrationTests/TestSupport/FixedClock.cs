using OpsFlow.Application.Abstractions;

namespace OpsFlow.Api.IntegrationTests.TestSupport;

internal sealed class FixedClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; } = utcNow;
}
