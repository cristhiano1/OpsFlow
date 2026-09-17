namespace OpsFlow.Application.Documents;

/// <summary>
/// No-op <see cref="IGroundedAnswerTelemetry"/>. Used as the safe default when no
/// exporter is configured and in tests that do not assert telemetry. It never
/// allocates, blocks, or throws.
/// </summary>
public sealed class NullGroundedAnswerTelemetry : IGroundedAnswerTelemetry
{
    /// <summary>The shared singleton instance.</summary>
    public static readonly NullGroundedAnswerTelemetry Instance = new();

    private NullGroundedAnswerTelemetry()
    {
    }

    /// <inheritdoc />
    public void Record(in GroundedAnswerMeasurement measurement)
    {
        // Intentionally does nothing.
    }
}
