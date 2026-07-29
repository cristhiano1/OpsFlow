namespace OpsFlow.Application.Abstractions;

/// <summary>
/// Provides the current time. Injected so time-dependent logic remains testable
/// and never calls <c>DateTimeOffset.UtcNow</c> directly in business code.
/// </summary>
public interface IClock
{
    /// <summary>The current instant in UTC.</summary>
    DateTimeOffset UtcNow { get; }
}
