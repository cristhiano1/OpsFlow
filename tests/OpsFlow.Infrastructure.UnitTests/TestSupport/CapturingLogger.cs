using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OpsFlow.Infrastructure.UnitTests.TestSupport;

/// <summary>Records every formatted log message for assertions.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages.Enqueue(formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
