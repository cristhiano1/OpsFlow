using OpsFlow.Application.Abstractions;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FixedClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; } = utcNow;
}
