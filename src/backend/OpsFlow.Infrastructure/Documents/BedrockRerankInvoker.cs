using System.Diagnostics;
using Amazon;
using Amazon.BedrockAgentRuntime;
using Amazon.BedrockAgentRuntime.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Configuration;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// Amazon Bedrock implementation of <see cref="IBedrockRerankInvoker"/>. This is
/// the only production file that understands AWS Bedrock rerank request/response
/// and exception types; it maps them to provider-neutral values and translates
/// every AWS/transport failure into <see cref="ChunkRerankingException"/> so AWS
/// SDK types never escape into the adapter or the Application layer. Credentials
/// are resolved by the AWS SDK default credential chain — none are read from
/// configuration. Safe for singleton lifetime.
/// </summary>
internal sealed partial class BedrockRerankInvoker : IBedrockRerankInvoker, IDisposable
{
    private readonly IAmazonBedrockAgentRuntime _client;
    private readonly bool _ownsClient;
    private readonly string _modelArn;
    private readonly string _modelId;
    private readonly TimeSpan _requestTimeout;
    private readonly ILogger<BedrockRerankInvoker> _logger;

    /// <summary>
    /// Production constructor. Builds an <see cref="AmazonBedrockAgentRuntimeClient"/>
    /// for the configured region, relying on the AWS default credential chain. The
    /// per-request timeout is enforced in <see cref="RerankAsync"/> with a linked
    /// cancellation token, because the AWS SDK client timeout does not bound async
    /// operations. This invoker owns and disposes that client.
    /// </summary>
    public BedrockRerankInvoker(
        IOptions<BedrockRerankerOptions> options,
        ILogger<BedrockRerankInvoker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var value = options.Value;
        _modelId = value.ModelId;
        _modelArn = BuildModelArn(value.Region, value.ModelId);
        _requestTimeout = TimeSpan.FromSeconds(value.TimeoutSeconds);
        _logger = logger;

        // AmazonBedrockAgentRuntimeConfig.Timeout does not bound async SDK calls,
        // so it is intentionally not set here; the per-request timeout is applied
        // via a linked cancellation token in RerankAsync instead.
        var config = new AmazonBedrockAgentRuntimeConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(value.Region),
        };

        // No credentials are passed: the client resolves them from the AWS
        // default credential chain (environment, shared profile, SSO, ECS task
        // role, or IAM workload identity).
        _client = new AmazonBedrockAgentRuntimeClient(config);
        _ownsClient = true;
    }

    /// <summary>
    /// Test constructor — accepts an externally owned client for transport
    /// injection. The injected client is not disposed by this invoker.
    /// </summary>
    internal BedrockRerankInvoker(
        IAmazonBedrockAgentRuntime client,
        IOptions<BedrockRerankerOptions> options,
        ILogger<BedrockRerankInvoker> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var value = options.Value;
        _client = client;
        _ownsClient = false;
        _modelId = value.ModelId;
        _modelArn = BuildModelArn(value.Region, value.ModelId);
        _requestTimeout = TimeSpan.FromSeconds(value.TimeoutSeconds);
        _logger = logger;
    }

    /// <summary>Builds the Bedrock foundation-model ARN from region and model id.</summary>
    internal static string BuildModelArn(string region, string modelId) =>
        $"arn:aws:bedrock:{region}::foundation-model/{modelId}";

    /// <inheritdoc />
    public async Task<IReadOnlyList<BedrockRerankResult>> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documents);

        var request = BuildRequest(query, documents);

        // Enforce the configured per-request timeout with a linked token, since
        // AmazonBedrockAgentRuntimeConfig.Timeout does not bound async SDK calls.
        // The original caller token stays authoritative when classifying caller
        // cancellation versus a local timeout below.
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCts.CancelAfter(_requestTimeout);

        var stopwatch = Stopwatch.StartNew();
        RerankResponse response;
        try
        {
            response = await _client.RerankAsync(request, requestCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller-requested cancellation propagates unchanged. Checked first so
            // caller cancellation always wins over the linked local timeout.
            throw;
        }
        catch (OperationCanceledException ex) when (requestCts.IsCancellationRequested)
        {
            // The configured per-request timeout fired while the caller's token
            // was NOT cancelled: a reranker failure, not caller cancellation.
            LogRerankFailed(ex, _modelId);
            throw new ChunkRerankingException(
                "The Bedrock reranker timed out while scoring the candidates.", ex);
        }
        catch (Exception ex)
        {
            // Covers AmazonServiceException (access denied, validation, resource
            // not found, throttling, quota, service 5xx), AmazonClientException
            // (network/SDK), HttpRequestException, and any provider-local
            // cancellation not tied to the caller token or the local timeout. The
            // message is categorical and carries no query text, candidate text,
            // request payload, or credentials.
            LogRerankFailed(ex, _modelId);
            throw new ChunkRerankingException(
                "The Bedrock reranker failed to score the candidates.", ex);
        }

        var results = MapResults(response);
        stopwatch.Stop();
        LogRerankCompleted(_modelId, documents.Count, results.Count, stopwatch.ElapsedMilliseconds);
        return results;
    }

    private RerankRequest BuildRequest(string query, IReadOnlyList<string> documents)
    {
        var sources = new List<RerankSource>(documents.Count);
        foreach (var text in documents)
        {
            sources.Add(new RerankSource
            {
                Type = RerankSourceType.INLINE,
                InlineDocumentSource = new RerankDocument
                {
                    Type = RerankDocumentType.TEXT,
                    TextDocument = new RerankTextDocument { Text = text },
                },
            });
        }

        return new RerankRequest
        {
            Queries =
            [
                new RerankQuery
                {
                    Type = RerankQueryContentType.TEXT,
                    TextQuery = new RerankTextDocument { Text = query },
                },
            ],
            Sources = sources,
            RerankingConfiguration = new RerankingConfiguration
            {
                Type = RerankingConfigurationType.BEDROCK_RERANKING_MODEL,
                BedrockRerankingConfiguration = new BedrockRerankingConfiguration
                {
                    ModelConfiguration = new BedrockRerankingModelConfiguration
                    {
                        ModelArn = _modelArn,
                    },
                    // Request a score for every supplied document so the
                    // Application layer's exact-count validation can hold.
                    NumberOfResults = documents.Count,
                },
            },
        };
    }

    private static List<BedrockRerankResult> MapResults(RerankResponse response)
    {
        if (response?.Results is null)
        {
            throw new ChunkRerankingException("Bedrock returned no rerank results.");
        }

        var mapped = new List<BedrockRerankResult>(response.Results.Count);
        foreach (var result in response.Results)
        {
            if (result is null || !result.Index.HasValue || !result.RelevanceScore.HasValue)
            {
                // A result without an index or score cannot be correlated or
                // scored. Fail closed rather than invent a value.
                throw new ChunkRerankingException(
                    "Bedrock returned a rerank result without an index or relevance score.");
            }

            mapped.Add(new BedrockRerankResult(result.Index.Value, result.RelevanceScore.Value));
        }

        return mapped;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Bedrock rerank completed: model={Model}, candidates={CandidateCount}, results={ResultCount}, elapsed={ElapsedMs}ms")]
    private partial void LogRerankCompleted(string model, int candidateCount, int resultCount, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Bedrock rerank failed: model={Model}")]
    private partial void LogRerankFailed(Exception exception, string model);
}
