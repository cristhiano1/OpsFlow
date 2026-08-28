using System.ClientModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Embeddings;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Configuration;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// OpenAI-backed <see cref="IEmbeddingGenerator"/> using the official SDK.
/// Fixed to <c>text-embedding-3-small</c> / 1536 dimensions for the
/// <c>opsflow-semantic-v1</c> profile. Safe for singleton lifetime.
/// </summary>
public sealed partial class OpenAIEmbeddingGenerator : IEmbeddingGenerator
{
    private const string ModelId = "text-embedding-3-small";
    private const int BatchSize = 60;

    private readonly EmbeddingClient? _client;
    private readonly ILogger<OpenAIEmbeddingGenerator> _logger;

    /// <inheritdoc />
    public EmbeddingGeneratorIdentity Identity { get; } = new(
        EmbeddingProfiles.SemanticV1Id,
        ModelId,
        EmbeddingProfiles.SemanticV1Dimensions);

    /// <summary>Production constructor — creates the SDK client when an API key is configured.</summary>
    public OpenAIEmbeddingGenerator(
        IOptions<OpenAIEmbeddingOptions> options,
        ILogger<OpenAIEmbeddingGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        var apiKey = options.Value.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var clientOptions = new OpenAIClientOptions
            {
                NetworkTimeout = TimeSpan.FromSeconds(60),
            };
            _client = new EmbeddingClient(ModelId, new ApiKeyCredential(apiKey), clientOptions);
        }
    }

    /// <summary>Test constructor — accepts a pre-configured client for transport injection.</summary>
    internal OpenAIEmbeddingGenerator(
        EmbeddingClient client,
        ILogger<OpenAIEmbeddingGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Count == 0)
        {
            return [];
        }

        if (_client is null)
        {
            throw new EmbeddingGenerationException(
                "OpenAI embedding generation is not configured. Set the OpenAI:ApiKey configuration value.");
        }

        int batchCount = (texts.Count + BatchSize - 1) / BatchSize;

        LogGenerationStarted(ModelId, texts.Count, batchCount);

        var stopwatch = Stopwatch.StartNew();
        var results = new List<ReadOnlyMemory<float>>(texts.Count);

        var embeddingOptions = new EmbeddingGenerationOptions
        {
            Dimensions = EmbeddingProfiles.SemanticV1Dimensions,
        };

        try
        {
            for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int start = batchIndex * BatchSize;
                int count = Math.Min(BatchSize, texts.Count - start);

                var batch = new List<string>(count);
                for (int i = start; i < start + count; i++)
                {
                    batch.Add(texts[i]);
                }

                ClientResult<OpenAIEmbeddingCollection> response =
                    await _client.GenerateEmbeddingsAsync(batch, embeddingOptions, cancellationToken);

                OpenAIEmbeddingCollection embeddings = response.Value;

                if (embeddings.Count != count)
                {
                    throw new EmbeddingGenerationException(
                        $"Provider returned {embeddings.Count} embeddings for batch {batchIndex} but expected {count}.");
                }

                var ordered = new ReadOnlyMemory<float>[count];
                var indexSeen = new bool[count];

                foreach (OpenAIEmbedding embedding in embeddings)
                {
                    int idx = embedding.Index;

                    if (idx < 0 || idx >= count)
                    {
                        throw new EmbeddingGenerationException(
                            $"Provider returned embedding with index {idx} outside expected range [0, {count}) in batch {batchIndex}.");
                    }

                    if (indexSeen[idx])
                    {
                        throw new EmbeddingGenerationException(
                            $"Provider returned duplicate embedding index {idx} in batch {batchIndex}.");
                    }

                    indexSeen[idx] = true;
                    ordered[idx] = embedding.ToFloats();
                }

                results.AddRange(ordered);
            }

            stopwatch.Stop();

            LogGenerationCompleted(ModelId, texts.Count, batchCount, stopwatch.ElapsedMilliseconds);

            return results;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EmbeddingGenerationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogGenerationFailed(ex, ModelId, texts.Count);

            throw new EmbeddingGenerationException(
                $"Embedding generation failed for {texts.Count} inputs using model '{ModelId}'.", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Generating embeddings: model={Model}, inputs={InputCount}, batches={BatchCount}")]
    private partial void LogGenerationStarted(string model, int inputCount, int batchCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Embedding generation completed: model={Model}, inputs={InputCount}, batches={BatchCount}, elapsed={ElapsedMs}ms")]
    private partial void LogGenerationCompleted(string model, int inputCount, int batchCount, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Embedding generation failed: model={Model}, inputs={InputCount}")]
    private partial void LogGenerationFailed(Exception exception, string model, int inputCount);
}
