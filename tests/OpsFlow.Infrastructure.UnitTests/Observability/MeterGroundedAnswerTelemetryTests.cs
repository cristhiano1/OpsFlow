using System.Diagnostics.Metrics;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Observability;

namespace OpsFlow.Infrastructure.UnitTests.Observability;

public sealed class MeterGroundedAnswerTelemetryTests
{
    private sealed record Recorded(string Instrument, double Value, Dictionary<string, object?> Tags);

    private static readonly HashSet<string> AllowedTagKeys =
        new(StringComparer.Ordinal) { "result.status", "retrieval.mode", "error.category" };

    private static readonly HashSet<string> AllowedTagValues = new(StringComparer.Ordinal)
    {
        // result.status
        "answered", "insufficient_evidence", "project_not_found", "failed", "canceled",
        // retrieval.mode
        "hybrid", "reranked", "hybrid_fallback", "not_applicable",
        // error.category
        "none", "embedding_provider", "reranker_operational", "reranker_validation",
        "answer_provider", "answer_validation", "internal",
    };

    private static (List<Recorded> Recorded, MeterListener Listener) StartListener()
    {
        var recorded = new List<Recorded>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == MeterGroundedAnswerTelemetry.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
            recorded.Add(new Recorded(inst.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<int>((inst, value, tags, _) =>
            recorded.Add(new Recorded(inst.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<double>((inst, value, tags, _) =>
            recorded.Add(new Recorded(inst.Name, value, ToDictionary(tags))));
        listener.Start();
        return (recorded, listener);
    }

    private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            dictionary[tag.Key] = tag.Value;
        }

        return dictionary;
    }

    [Fact]
    public void Answered_measurement_emits_bounded_request_duration_and_evidence_instruments()
    {
        using var telemetry = new MeterGroundedAnswerTelemetry();
        var (recorded, listener) = StartListener();
        using (listener)
        {
            telemetry.Record(new GroundedAnswerMeasurement(
                AnswerPipelineOutcome.Answered,
                AnswerRetrievalMode.Hybrid,
                AnswerFailureCategory.None,
                SelectedEvidenceCount: 3,
                TotalDuration: TimeSpan.FromMilliseconds(120),
                RetrievalDuration: TimeSpan.FromMilliseconds(40),
                GenerationDuration: TimeSpan.FromMilliseconds(60)));
        }

        var requests = Assert.Single(recorded, r => r.Instrument == "opsflow.rag.answer.requests");
        Assert.Equal(1, requests.Value);
        Assert.Equal("answered", requests.Tags["result.status"]);
        Assert.Equal("hybrid", requests.Tags["retrieval.mode"]);

        Assert.Contains(recorded, r => r.Instrument == "opsflow.rag.answer.duration");
        Assert.Contains(recorded, r => r.Instrument == "opsflow.rag.retrieval.duration");
        Assert.Contains(recorded, r => r.Instrument == "opsflow.rag.generation.duration");

        var evidence = Assert.Single(recorded, r => r.Instrument == "opsflow.rag.evidence.selected");
        Assert.Equal(3, evidence.Value);

        // A successful answer emits no failure counter.
        Assert.DoesNotContain(recorded, r => r.Instrument == "opsflow.rag.answer.failures");
    }

    [Fact]
    public void Failed_measurement_emits_failure_counter_with_bounded_error_category()
    {
        using var telemetry = new MeterGroundedAnswerTelemetry();
        var (recorded, listener) = StartListener();
        using (listener)
        {
            telemetry.Record(new GroundedAnswerMeasurement(
                AnswerPipelineOutcome.Failed,
                AnswerRetrievalMode.Reranked,
                AnswerFailureCategory.RerankerValidation,
                SelectedEvidenceCount: null,
                TotalDuration: TimeSpan.FromMilliseconds(15),
                RetrievalDuration: TimeSpan.FromMilliseconds(15),
                GenerationDuration: null));
        }

        var failures = Assert.Single(recorded, r => r.Instrument == "opsflow.rag.answer.failures");
        Assert.Equal(1, failures.Value);
        Assert.Equal("reranker_validation", failures.Tags["error.category"]);

        var requests = Assert.Single(recorded, r => r.Instrument == "opsflow.rag.answer.requests");
        Assert.Equal("failed", requests.Tags["result.status"]);
        Assert.Equal("reranked", requests.Tags["retrieval.mode"]);

        // Generation never ran and no evidence was selected: those instruments are not recorded.
        Assert.DoesNotContain(recorded, r => r.Instrument == "opsflow.rag.generation.duration");
        Assert.DoesNotContain(recorded, r => r.Instrument == "opsflow.rag.evidence.selected");
    }

    [Fact]
    public void Not_applicable_measurement_records_retrieval_duration_only_for_phase_histograms()
    {
        using var telemetry = new MeterGroundedAnswerTelemetry();
        var (recorded, listener) = StartListener();
        using (listener)
        {
            telemetry.Record(new GroundedAnswerMeasurement(
                AnswerPipelineOutcome.ProjectNotFound,
                AnswerRetrievalMode.NotApplicable,
                AnswerFailureCategory.None,
                SelectedEvidenceCount: null,
                TotalDuration: TimeSpan.FromMilliseconds(5),
                RetrievalDuration: TimeSpan.FromMilliseconds(5),
                GenerationDuration: null));
        }

        Assert.Contains(recorded, r => r.Instrument == "opsflow.rag.retrieval.duration");
        Assert.DoesNotContain(recorded, r => r.Instrument == "opsflow.rag.generation.duration");
        Assert.DoesNotContain(recorded, r => r.Instrument == "opsflow.rag.evidence.selected");
    }

    [Fact]
    public void Every_emitted_tag_uses_only_bounded_keys_and_values()
    {
        using var telemetry = new MeterGroundedAnswerTelemetry();
        var (recorded, listener) = StartListener();
        using (listener)
        {
            // Exercise each outcome/mode/category so all tag values are produced.
            telemetry.Record(new GroundedAnswerMeasurement(
                AnswerPipelineOutcome.Answered, AnswerRetrievalMode.Reranked, AnswerFailureCategory.None,
                2, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(6)));
            telemetry.Record(new GroundedAnswerMeasurement(
                AnswerPipelineOutcome.Failed, AnswerRetrievalMode.HybridFallback, AnswerFailureCategory.Internal,
                null, TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(3), null));
            telemetry.Record(new GroundedAnswerMeasurement(
                AnswerPipelineOutcome.Canceled, AnswerRetrievalMode.Hybrid, AnswerFailureCategory.None,
                null, TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(2), null));
        }

        Assert.NotEmpty(recorded);
        foreach (var measurement in recorded)
        {
            foreach (var tag in measurement.Tags)
            {
                Assert.Contains(tag.Key, AllowedTagKeys);
                var value = Assert.IsType<string>(tag.Value);
                Assert.Contains(value, AllowedTagValues);
            }
        }
    }
}
