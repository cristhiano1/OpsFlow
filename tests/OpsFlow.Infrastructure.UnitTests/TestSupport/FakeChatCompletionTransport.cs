using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Infrastructure.UnitTests.TestSupport;

/// <summary>
/// In-memory HTTP transport seam for exercising
/// <see cref="OpenAiGroundedAnswerGenerator"/> without any real network call.
/// Records outgoing chat-completion requests and returns scripted responses in
/// the OpenAI chat-completions wire format.
/// </summary>
internal sealed class FakeChatCompletionHandler : HttpMessageHandler
{
    private readonly Func<string, HttpResponseMessage> _onRequest;

    public FakeChatCompletionHandler(Func<string, HttpResponseMessage> onRequest)
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

        return _onRequest(body);
    }
}

/// <summary>Zero-delay retry policy so retryable-error tests do not sleep on backoff.</summary>
internal sealed class NoDelayChatRetryPolicy : ClientRetryPolicy
{
    public NoDelayChatRetryPolicy(int maxRetries) : base(maxRetries) { }

    protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount) => TimeSpan.Zero;
}

/// <summary>Helpers for building fake chat responses and wiring the SUT.</summary>
internal static class FakeChatCompletion
{
    public const string Model = "gpt-4o-mini";

    /// <summary>A chat.completion response whose assistant message content is <paramref name="content"/>.</summary>
    public static string ResponseBody(string? content, string finishReason = "stop", string? refusal = null)
    {
        var contentJson = content is null ? "null" : JsonSerializer.Serialize(content);
        var refusalJson = refusal is null ? "null" : JsonSerializer.Serialize(refusal);
        return
            "{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion\",\"created\":1700000000," +
            "\"model\":\"" + Model + "\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\"," +
            "\"content\":" + contentJson + ",\"refusal\":" + refusalJson + "}," +
            "\"finish_reason\":\"" + finishReason + "\"}]," +
            "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";
    }

    /// <summary>Builds the structured grounded-answer JSON that lives inside the assistant content.</summary>
    public static string StructuredContent(string status, string? answer, params int[] citations)
    {
        var answerJson = answer is null ? "null" : JsonSerializer.Serialize(answer);
        var citationsJson = "[" + string.Join(",", citations) + "]";
        return $"{{\"status\":\"{status}\",\"answer\":{answerJson},\"citations\":{citationsJson}}}";
    }

    /// <summary>An HTTP 200 response carrying the given assistant content.</summary>
    public static HttpResponseMessage Ok(string? content, string finishReason = "stop", string? refusal = null) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(ResponseBody(content, finishReason, refusal), Encoding.UTF8, "application/json"),
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

    /// <summary>Creates a <see cref="ChatClient"/> bound to a fake handler and zero-delay retries.</summary>
    public static ChatClient Client(FakeChatCompletionHandler handler, int maxRetries = 3)
    {
        var transport = new HttpClientPipelineTransport(new HttpClient(handler));
        var options = new OpenAIClientOptions
        {
            Transport = transport,
            NetworkTimeout = TimeSpan.FromSeconds(5),
            RetryPolicy = new NoDelayChatRetryPolicy(maxRetries),
        };
        return new ChatClient(Model, new ApiKeyCredential("sk-test"), options);
    }

    /// <summary>Constructs the SUT over a fake client (internal test constructor).</summary>
    public static OpenAiGroundedAnswerGenerator Sut(
        FakeChatCompletionHandler handler,
        ILogger<OpenAiGroundedAnswerGenerator>? logger = null,
        int maxRetries = 3) =>
        new(Client(handler, maxRetries),
            Model,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiGroundedAnswerGenerator>.Instance);
}
