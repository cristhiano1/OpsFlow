using OpsFlow.Application.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

/// <summary>
/// Test <see cref="IGroundedAnswerTelemetry"/> that captures every recorded
/// measurement. It can optionally throw to prove the service never lets a
/// misbehaving telemetry sink affect the answer path.
/// </summary>
internal sealed class RecordingGroundedAnswerTelemetry : IGroundedAnswerTelemetry
{
    private readonly List<GroundedAnswerMeasurement> _measurements = [];

    /// <summary>When set, thrown on every <see cref="Record"/> call.</summary>
    public Exception? ExceptionToThrow { get; set; }

    public IReadOnlyList<GroundedAnswerMeasurement> Measurements => _measurements;

    public GroundedAnswerMeasurement Single => Assert.Single(_measurements);

    public void Record(in GroundedAnswerMeasurement measurement)
    {
        _measurements.Add(measurement);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }
    }
}
