using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Configuration;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// OpenAI-backed <see cref="IGroundedAnswerGenerator"/> using the official SDK's
/// chat completions with strict JSON-schema structured output. The adapter owns
/// only provider interaction and parsing into the neutral
/// <see cref="GroundedAnswerGenerationOutput"/>; grounding policy, prompt
/// construction, and citation validation live in the Application layer. Safe for
/// singleton lifetime.
/// </summary>
public sealed partial class OpenAiGroundedAnswerGenerator : IGroundedAnswerGenerator
{
    /// <summary>Structured-output schema name sent to the provider.</summary>
    internal const string SchemaName = "opsflow_grounded_answer_v1";

    /// <summary>Output token cap for a grounded answer.</summary>
    internal const int MaxOutputTokens = 800;

    private const string SchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["status", "answer", "citations"],
          "properties": {
            "status": { "type": "string", "enum": ["answered", "insufficient_evidence"] },
            "answer": { "type": ["string", "null"] },
            "citations": { "type": "array", "items": { "type": "integer" } }
          }
        }
        """;

    private static readonly ChatResponseFormat GroundedAnswerResponseFormat =
        ChatResponseFormat.CreateJsonSchemaFormat(
            SchemaName,
            BinaryData.FromString(SchemaJson),
            jsonSchemaFormatDescription:
                "A grounded answer with a status, an optional answer, and integer evidence citation ids.",
            jsonSchemaIsStrict: true);

    private readonly ChatClient? _client;
    private readonly ILogger<OpenAiGroundedAnswerGenerator> _logger;
    private readonly string _model;
    private readonly string? _configurationErrorMessage;

    /// <summary>Production constructor — creates the SDK client when an API key and model are configured.</summary>
    public OpenAiGroundedAnswerGenerator(
        IOptions<OpenAIAnswerGenerationOptions> options,
        ILogger<OpenAiGroundedAnswerGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        var value = options.Value;
        _model = value.AnswerModel ?? string.Empty;

        if (string.IsNullOrWhiteSpace(value.ApiKey))
        {
            _configurationErrorMessage =
                "OpenAI answer generation is not configured. Set the OpenAI:ApiKey configuration value.";
        }
        else if (string.IsNullOrWhiteSpace(value.AnswerModel))
        {
            _configurationErrorMessage =
                "OpenAI answer generation is not configured. Set the OpenAI:AnswerModel configuration value.";
        }
        else
        {
            var clientOptions = new OpenAIClientOptions
            {
                NetworkTimeout = TimeSpan.FromSeconds(60),
            };
            _client = new ChatClient(value.AnswerModel, new ApiKeyCredential(value.ApiKey), clientOptions);
        }
    }

    /// <summary>Test constructor — accepts a pre-configured client for transport injection.</summary>
    internal OpenAiGroundedAnswerGenerator(
        ChatClient client,
        string model,
        ILogger<OpenAiGroundedAnswerGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _model = model;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GroundedAnswerGenerationOutput> GenerateAsync(
        GroundedAnswerGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_client is null)
        {
            throw new AnswerGenerationException(
                _configurationErrorMessage ?? "OpenAI answer generation is not configured.");
        }

        ChatMessage[] messages =
        [
            new SystemChatMessage(request.SystemPrompt),
            new UserChatMessage(request.UserPrompt),
        ];

        var options = new ChatCompletionOptions
        {
            Temperature = 0f,
            MaxOutputTokenCount = MaxOutputTokens,
            ResponseFormat = GroundedAnswerResponseFormat,
        };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            ClientResult<ChatCompletion> result =
                await _client.CompleteChatAsync(messages, options, cancellationToken);

            var output = ParseCompletion(result.Value);

            stopwatch.Stop();
            LogGenerationCompleted(
                _model, output.Status, output.CitationNumbers.Count, stopwatch.ElapsedMilliseconds);

            return output;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AnswerGenerationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogGenerationFailed(ex, _model);

            throw new AnswerGenerationException(
                $"Answer generation failed using model '{_model}'.", ex);
        }
    }

    private static GroundedAnswerGenerationOutput ParseCompletion(ChatCompletion completion)
    {
        // The SDK surfaces refusals separately from ordinary content.
        if (!string.IsNullOrEmpty(completion.Refusal))
        {
            throw new AnswerGenerationException("Answer generation was refused by the provider.");
        }

        if (completion.FinishReason == ChatFinishReason.Length)
        {
            throw new AnswerGenerationException(
                "Answer generation response was truncated before completion (length finish reason).");
        }

        if (completion.FinishReason != ChatFinishReason.Stop)
        {
            throw new AnswerGenerationException(
                $"Answer generation ended with an unusable finish reason '{completion.FinishReason}'.");
        }

        var text = ExtractText(completion);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new AnswerGenerationException("Answer generation returned empty content.");
        }

        return ParseStructuredJson(text);
    }

    private static string? ExtractText(ChatCompletion completion)
    {
        var content = completion.Content;
        if (content is null || content.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var part in content)
        {
            if (part.Kind == ChatMessageContentPartKind.Text)
            {
                _ = builder.Append(part.Text);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static GroundedAnswerGenerationOutput ParseStructuredJson(string json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new AnswerGenerationException("Answer generation returned malformed JSON.", ex);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AnswerGenerationException("Answer generation JSON root is not an object.");
        }

        if (!root.TryGetProperty("status", out var statusElement)
            || statusElement.ValueKind != JsonValueKind.String)
        {
            throw new AnswerGenerationException("Answer generation JSON is missing a string 'status'.");
        }

        var status = statusElement.GetString() switch
        {
            "answered" => GeneratedAnswerStatus.Answered,
            "insufficient_evidence" => GeneratedAnswerStatus.InsufficientEvidence,
            _ => throw new AnswerGenerationException(
                $"Answer generation JSON has an unknown status '{statusElement.GetString()}'."),
        };

        if (!root.TryGetProperty("answer", out var answerElement))
        {
            throw new AnswerGenerationException("Answer generation JSON is missing 'answer'.");
        }

        string? answer;
        if (answerElement.ValueKind == JsonValueKind.String)
        {
            answer = answerElement.GetString();
        }
        else if (answerElement.ValueKind == JsonValueKind.Null)
        {
            answer = null;
        }
        else
        {
            throw new AnswerGenerationException("Answer generation JSON 'answer' is not a string or null.");
        }

        if (!root.TryGetProperty("citations", out var citationsElement)
            || citationsElement.ValueKind != JsonValueKind.Array)
        {
            throw new AnswerGenerationException("Answer generation JSON is missing an array 'citations'.");
        }

        var citations = new List<int>();
        foreach (var element in citationsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number))
            {
                throw new AnswerGenerationException(
                    "Answer generation JSON 'citations' contains a non-integer value.");
            }

            citations.Add(number);
        }

        return new GroundedAnswerGenerationOutput(status, answer, citations);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Grounded answer generated: model={Model}, status={Status}, citations={CitationCount}, elapsed={ElapsedMs}ms")]
    private partial void LogGenerationCompleted(string model, GeneratedAnswerStatus status, int citationCount, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Grounded answer generation failed: model={Model}")]
    private partial void LogGenerationFailed(Exception exception, string model);
}
