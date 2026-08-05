using OpsFlow.Application.Abstractions;

namespace OpsFlow.Api.IntegrationTests.TestSupport;

/// <summary>
/// Test double for <see cref="IClock"/> that returns pre-configured timestamps
/// in FIFO order. Each call to <see cref="UtcNow"/> dequeues the next value; if
/// the queue is empty the last value is returned repeatedly so the clock
/// remains defined even if a test-under-observation reads it more times than
/// the test explicitly configured.
/// <para>
/// This lets a test simulate time elapsing between two clock reads without any
/// wall-clock delay or Thread.Sleep — for example, the pre-lock initial read
/// vs. the post-lock authoritative read inside RefreshSessionRotator.
/// </para>
/// </summary>
internal sealed class QueuedClock : IClock
{
    private readonly Queue<DateTimeOffset> _values;
    private DateTimeOffset LastValue { get; set; }
    public int CallCount { get; private set; }

    public QueuedClock(params DateTimeOffset[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }
        _values = new Queue<DateTimeOffset>(values);
        LastValue = values[^1];
    }

    public DateTimeOffset UtcNow
    {
        get
        {
            CallCount++;
            if (_values.Count > 0)
            {
                LastValue = _values.Dequeue();
            }
            return LastValue;
        }
    }
}
