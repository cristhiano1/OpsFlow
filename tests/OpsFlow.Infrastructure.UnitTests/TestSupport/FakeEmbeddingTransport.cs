using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Embeddings;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Infrastructure.UnitTests.TestSupport;

/// <summary>
/// In-memory HTTP transport seam for exercising <see cref="OpenAIEmbeddingGenerator"/>
/// without any real network call. Records outgoing requests and returns scripted
/// responses. The response <c>embedding</c> field is base64-encoded little-endian
/// float32, matching the wire format the official SDK requests
/// (<c>encoding_format: base64</c>).
/// </summary>
internal sealed class FakeEmbeddingHandler : HttpMessageHandler
{
    private readonly Func<IReadOnlyList<string>, HttpResponseMessage> _onRequest;

    public FakeEmbeddingHandler(Func<IReadOnlyList<string>, HttpResponseMessage> onRequest)
    {
        _onRequest = onRequest;
    }

    /// <summary>Number of transport round-trips (includes SDK retries).</summary>
    public int CallCount { get; private set; }

    /// <summary>Raw request bodies in call order.</summary>
    public List<string> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;

        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        RequestBodies.Add(body);

        var inputs = ParseInputs(body);
        return _onRequest(inputs);
    }

    private static List<string> ParseInputs(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return [];
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>(input.GetArrayLength());
        foreach (var element in input.EnumerateArray())
        {
            result.Add(element.GetString() ?? string.Empty);
        }
        return result;
    }
}

/// <summary>Zero-delay retry policy so retryable-error tests do not sleep on backoff.</summary>
internal sealed class NoDelayRetryPolicy : ClientRetryPolicy
{
    public NoDelayRetryPolicy(int maxRetries) : base(maxRetries) { }

    protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount) => TimeSpan.Zero;
}

/// <summary>Helpers for building fake embedding responses and wiring the SUT.</summary>
internal static class FakeEmbedding
{
    /// <summary>Builds a JSON embeddings response body from explicit (index, vector) entries.</summary>
    public static string ResponseBody(IEnumerable<(int Index, float[] Vector)> entries)
    {
        var sb = new StringBuilder();
        sb.Append("{\"object\":\"list\",\"data\":[");

        var first = true;
        foreach (var (index, vector) in entries)
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;

            sb.Append("{\"object\":\"embedding\",\"index\":")
              .Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"embedding\":\"")
              .Append(Base64(vector))
              .Append("\"}");
        }

        sb.Append("],\"model\":\"text-embedding-3-small\",")
          .Append("\"usage\":{\"prompt_tokens\":1,\"total_tokens\":1}}");
        return sb.ToString();
    }

    /// <summary>An HTTP 200 response carrying the given embedding entries.</summary>
    public static HttpResponseMessage Ok(IEnumerable<(int Index, float[] Vector)> entries) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(ResponseBody(entries), Encoding.UTF8, "application/json"),
        };

    /// <summary>An HTTP error response with a minimal OpenAI-style error body.</summary>
    public static HttpResponseMessage Error(HttpStatusCode status) =>
        new(status)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":\"fake error\",\"type\":\"fake\"}}",
                Encoding.UTF8,
                "application/json"),
        };

    /// <summary>Base64-encodes a float vector as little-endian IEEE-754 single precision.</summary>
    public static string Base64(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        if (!BitConverter.IsLittleEndian)
        {
            for (var i = 0; i < bytes.Length; i += sizeof(float))
            {
                Array.Reverse(bytes, i, sizeof(float));
            }
        }
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Creates an <see cref="EmbeddingClient"/> bound to a fake handler and zero-delay retries.</summary>
    public static EmbeddingClient Client(FakeEmbeddingHandler handler, int maxRetries = 3)
    {
        var transport = new HttpClientPipelineTransport(new HttpClient(handler));
        var options = new OpenAIClientOptions
        {
            Transport = transport,
            NetworkTimeout = TimeSpan.FromSeconds(5),
            RetryPolicy = new NoDelayRetryPolicy(maxRetries),
        };
        return new EmbeddingClient("text-embedding-3-small", new ApiKeyCredential("sk-test"), options);
    }

    /// <summary>Constructs the SUT over a fake client (internal test constructor).</summary>
    public static OpenAIEmbeddingGenerator Sut(
        FakeEmbeddingHandler handler,
        ILogger<OpenAIEmbeddingGenerator>? logger = null,
        int maxRetries = 3) =>
        new(Client(handler, maxRetries),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAIEmbeddingGenerator>.Instance);
}
