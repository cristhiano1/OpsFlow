using System.Net;
using System.Text;
using System.Globalization;
using Amazon;
using Amazon.BedrockAgentRuntime;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Infrastructure.UnitTests.TestSupport;

// IAmazonBedrockAgentRuntime is far too broad to hand-implement without hundreds
// of irrelevant stub methods. Per the implementation gate, we therefore use the
// AWS SDK's own transport seam: a fake HttpMessageHandler behind a real
// AmazonBedrockAgentRuntimeClient. This exercises the real request marshalling
// and response/exception unmarshalling while making no network call, and lets a
// test assert the exact serialized wire payload (a stronger no-egress proof than
// inspecting an in-memory request object).

/// <summary>
/// In-memory HTTP transport for exercising <see cref="BedrockRerankInvoker"/>
/// without any real AWS call. Records outgoing request bodies and returns a
/// scripted response (or throws a scripted transport exception).
/// </summary>
internal sealed class FakeBedrockRerankHandler : HttpMessageHandler
{
    private readonly Func<string, HttpResponseMessage> _onRequest;

    public FakeBedrockRerankHandler(Func<string, HttpResponseMessage> onRequest)
    {
        _onRequest = onRequest;
    }

    /// <summary>Number of transport round-trips (includes any SDK retries).</summary>
    public int CallCount { get; private set; }

    /// <summary>Raw request bodies in call order.</summary>
    public List<string> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;

        // Honor cancellation like a real transport so caller-cancellation paths
        // are exercised faithfully.
        cancellationToken.ThrowIfCancellationRequested();

        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        RequestBodies.Add(body);

        return _onRequest(body);
    }
}

/// <summary>A handler that throws a scripted exception instead of responding.</summary>
internal sealed class ThrowingBedrockHandler : HttpMessageHandler
{
    private readonly Func<Exception> _exceptionFactory;

    public ThrowingBedrockHandler(Func<Exception> exceptionFactory)
    {
        _exceptionFactory = exceptionFactory;
    }

    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        throw _exceptionFactory();
    }
}

/// <summary>
/// A handler that never responds: it stays pending until the supplied
/// cancellation token is cancelled, then throws the resulting cancellation. Used
/// to prove the configured per-request timeout actually bounds the async call.
/// </summary>
internal sealed class PendingBedrockHandler : HttpMessageHandler
{
    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable: the request should have been cancelled.");
    }
}

/// <summary>Supplies the SDK a client bound to a fake handler; never caches or disposes it.</summary>
internal sealed class FakeBedrockHttpClientFactory : Amazon.Runtime.HttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeBedrockHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(_handler);

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;

    public override string GetConfigUniqueString(IClientConfig clientConfig) => "fake-bedrock";
}

/// <summary>Helpers for building fake Bedrock rerank responses and wiring the SUT.</summary>
internal static class FakeBedrock
{
    public const string Region = "eu-central-1";
    public const string ModelId = "cohere.rerank-v3-5:0";

    /// <summary>Builds a JSON rerank response body from (index, score) entries.</summary>
    public static string ResponseBody(IEnumerable<(int Index, double Score)> entries)
    {
        var sb = new StringBuilder();
        sb.Append("{\"results\":[");
        var first = true;
        foreach (var (index, score) in entries)
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;
            sb.Append("{\"index\":")
              .Append(index.ToString(CultureInfo.InvariantCulture))
              .Append(",\"relevanceScore\":")
              .Append(score.ToString("R", CultureInfo.InvariantCulture))
              .Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>An HTTP 200 rerank response carrying the given entries.</summary>
    public static HttpResponseMessage Ok(IEnumerable<(int Index, double Score)> entries) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(ResponseBody(entries), Encoding.UTF8, "application/json"),
        };

    /// <summary>An HTTP error response carrying the AWS error-type header the SDK maps to a typed exception.</summary>
    public static HttpResponseMessage Error(HttpStatusCode status, string errorType)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(
                "{\"message\":\"fake error\"}", Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("x-amzn-ErrorType", errorType);
        return response;
    }

    public static IOptions<BedrockRerankerOptions> Options(
        string region = Region, string modelId = ModelId, int timeoutSeconds = 30) =>
        Microsoft.Extensions.Options.Options.Create(new BedrockRerankerOptions
        {
            Region = region,
            ModelId = modelId,
            TimeoutSeconds = timeoutSeconds,
        });

    /// <summary>Builds a real Bedrock client over the supplied handler (no network, no retries).</summary>
    public static AmazonBedrockAgentRuntimeClient Client(HttpMessageHandler handler)
    {
        var config = new AmazonBedrockAgentRuntimeConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(Region),
            HttpClientFactory = new FakeBedrockHttpClientFactory(handler),
            MaxErrorRetry = 0,
        };
        return new AmazonBedrockAgentRuntimeClient(new BasicAWSCredentials("test", "test"), config);
    }

    /// <summary>
    /// Constructs the invoker SUT over a fake transport. The returned harness OWNS
    /// the underlying <see cref="AmazonBedrockAgentRuntimeClient"/> and disposes it
    /// on <see cref="IDisposable.Dispose"/>; the production invoker never disposes
    /// an externally injected client, so test ownership lives here.
    /// </summary>
    public static BedrockInvokerHarness Sut(HttpMessageHandler handler, int timeoutSeconds = 30) =>
        new(handler, timeoutSeconds);
}

/// <summary>
/// Test-owned harness around <see cref="BedrockRerankInvoker"/>. It owns and
/// disposes the externally injected <see cref="AmazonBedrockAgentRuntimeClient"/>,
/// keeping the production invoker's ownership rule (it never disposes injected
/// clients) unchanged. Use with <c>using</c> so the client is disposed per test.
/// </summary>
internal sealed class BedrockInvokerHarness : IDisposable
{
    private readonly AmazonBedrockAgentRuntimeClient _client;

    public BedrockInvokerHarness(HttpMessageHandler handler, int timeoutSeconds = 30)
    {
        _client = FakeBedrock.Client(handler);
        Invoker = new BedrockRerankInvoker(
            _client, FakeBedrock.Options(timeoutSeconds: timeoutSeconds), NullLogger<BedrockRerankInvoker>.Instance);
    }

    /// <summary>The system under test.</summary>
    public BedrockRerankInvoker Invoker { get; }

    public void Dispose() => _client.Dispose();
}
