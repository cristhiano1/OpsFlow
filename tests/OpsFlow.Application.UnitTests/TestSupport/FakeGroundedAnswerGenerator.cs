using OpsFlow.Application.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeGroundedAnswerGenerator : IGroundedAnswerGenerator
{
    /// <summary>Output returned when no exception is configured. Null triggers a null-output return.</summary>
    public GroundedAnswerGenerationOutput? Output { get; set; }

    /// <summary>When set, thrown instead of returning output.</summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>When true, GenerateAsync returns a null output to exercise defensive handling.</summary>
    public bool ReturnNull { get; set; }

    public int CallCount { get; private set; }
    public GroundedAnswerGenerationRequest? LastRequest { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }

    public Task<GroundedAnswerGenerationOutput> GenerateAsync(
        GroundedAnswerGenerationRequest request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        LastCancellationToken = cancellationToken;

        cancellationToken.ThrowIfCancellationRequested();

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        // Intentionally allows returning null to test the service's defensive
        // contract handling, despite the non-nullable signature.
        return Task.FromResult(ReturnNull ? null! : Output!);
    }
}
