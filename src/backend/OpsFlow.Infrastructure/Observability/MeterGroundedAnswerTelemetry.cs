using System.Diagnostics.Metrics;
using OpsFlow.Application.Documents;

namespace OpsFlow.Infrastructure.Observability;

/// <summary>
/// <see cref="IGroundedAnswerTelemetry"/> backed by the BCL
/// <see cref="System.Diagnostics.Metrics"/> API. It owns a single named
/// <see cref="Meter"/>; any OpenTelemetry/Prometheus exporter attaches to that meter
/// by name out of process — no exporter is required for these instruments to exist
/// (see ADR-012). It records only the bounded values carried by
/// <see cref="GroundedAnswerMeasurement"/>; no user or document content, and no
/// identifiers, ever reach an instrument tag.
/// </summary>
internal sealed class MeterGroundedAnswerTelemetry : IGroundedAnswerTelemetry, IDisposable
{
    /// <summary>The meter name that an exporter subscribes to.</summary>
    public const string MeterName = "OpsFlow.Rag";

    private const string InstrumentationVersion = "1.0.0";

    private readonly Meter _meter;
    private readonly Counter<long> _requests;
    private readonly Counter<long> _failures;
    private readonly Histogram<double> _answerDuration;
    private readonly Histogram<double> _retrievalDuration;
    private readonly Histogram<double> _generationDuration;
    private readonly Histogram<int> _selectedEvidence;

    public MeterGroundedAnswerTelemetry()
    {
        _meter = new Meter(MeterName, InstrumentationVersion);

        _requests = _meter.CreateCounter<long>(
            "opsflow.rag.answer.requests",
            unit: "{request}",
            description: "Grounded answer requests, tagged by result status and retrieval mode.");
        _failures = _meter.CreateCounter<long>(
            "opsflow.rag.answer.failures",
            unit: "{failure}",
            description: "Grounded answer pipeline failures, tagged by bounded error category.");
        _answerDuration = _meter.CreateHistogram<double>(
            "opsflow.rag.answer.duration",
            unit: "s",
            description: "Total grounded answer request duration in seconds.");
        _retrievalDuration = _meter.CreateHistogram<double>(
            "opsflow.rag.retrieval.duration",
            unit: "s",
            description: "Evidence retrieval phase duration in seconds.");
        _generationDuration = _meter.CreateHistogram<double>(
            "opsflow.rag.generation.duration",
            unit: "s",
            description: "Answer generation phase duration in seconds; recorded only when generation ran.");
        _selectedEvidence = _meter.CreateHistogram<int>(
            "opsflow.rag.evidence.selected",
            unit: "{chunk}",
            description: "Number of evidence chunks selected for generation; recorded only when selection ran.");
    }

    /// <inheritdoc />
    public void Record(in GroundedAnswerMeasurement measurement)
    {
        var status = StatusTag(measurement.Outcome);
        var mode = ModeTag(measurement.RetrievalMode);

        _requests.Add(
            1,
            new KeyValuePair<string, object?>("result.status", status),
            new KeyValuePair<string, object?>("retrieval.mode", mode));

        _answerDuration.Record(
            measurement.TotalDuration.TotalSeconds,
            new KeyValuePair<string, object?>("result.status", status),
            new KeyValuePair<string, object?>("retrieval.mode", mode));

        _retrievalDuration.Record(
            measurement.RetrievalDuration.TotalSeconds,
            new KeyValuePair<string, object?>("retrieval.mode", mode));

        if (measurement.GenerationDuration is { } generation)
        {
            _generationDuration.Record(generation.TotalSeconds);
        }

        if (measurement.SelectedEvidenceCount is { } evidenceCount)
        {
            _selectedEvidence.Record(evidenceCount);
        }

        if (measurement.Outcome == AnswerPipelineOutcome.Failed)
        {
            _failures.Add(
                1,
                new KeyValuePair<string, object?>("error.category", CategoryTag(measurement.FailureCategory)));
        }
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private static string StatusTag(AnswerPipelineOutcome outcome) => outcome switch
    {
        AnswerPipelineOutcome.Answered => "answered",
        AnswerPipelineOutcome.InsufficientEvidence => "insufficient_evidence",
        AnswerPipelineOutcome.ProjectNotFound => "project_not_found",
        AnswerPipelineOutcome.Failed => "failed",
        AnswerPipelineOutcome.Canceled => "canceled",
        _ => "unknown",
    };

    private static string ModeTag(AnswerRetrievalMode mode) => mode switch
    {
        AnswerRetrievalMode.Hybrid => "hybrid",
        AnswerRetrievalMode.Reranked => "reranked",
        AnswerRetrievalMode.HybridFallback => "hybrid_fallback",
        AnswerRetrievalMode.NotApplicable => "not_applicable",
        _ => "unknown",
    };

    private static string CategoryTag(AnswerFailureCategory category) => category switch
    {
        AnswerFailureCategory.None => "none",
        AnswerFailureCategory.EmbeddingProviderFailure => "embedding_provider",
        AnswerFailureCategory.RerankerOperational => "reranker_operational",
        AnswerFailureCategory.RerankerValidation => "reranker_validation",
        AnswerFailureCategory.AnswerProviderFailure => "answer_provider",
        AnswerFailureCategory.AnswerValidation => "answer_validation",
        AnswerFailureCategory.Internal => "internal",
        _ => "unknown",
    };
}
