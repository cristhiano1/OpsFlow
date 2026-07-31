using OpsFlow.Application.Abstractions;

namespace OpsFlow.Infrastructure.UnitTests.TestSupport;

/// <summary>Deterministic <see cref="IClock"/> for tests.</summary>
internal sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; }
}
