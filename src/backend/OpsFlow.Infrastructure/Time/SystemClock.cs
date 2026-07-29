using OpsFlow.Application.Abstractions;

namespace OpsFlow.Infrastructure.Time;

/// <summary>Default <see cref="IClock"/> implementation backed by the system clock.</summary>
internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
