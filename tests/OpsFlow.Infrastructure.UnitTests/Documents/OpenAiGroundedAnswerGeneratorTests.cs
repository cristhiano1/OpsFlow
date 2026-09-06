using System.Net;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Documents;
using OpsFlow.Infrastructure.UnitTests.TestSupport;

namespace OpsFlow.Infrastructure.UnitTests.Documents;

public sealed class OpenAiGroundedAnswerGeneratorTests
{
    private static readonly GroundedAnswerGenerationRequest Request =
        new("SYSTEM_POLICY_MARKER", "USER_PROMPT_MARKER");

    private static OpenAiGroundedAnswerGenerator ProductionSut(string? apiKey, string? model = "gpt-4o-mini") =>
        new(Options.Create(new OpenAIAnswerGenerationOptions { ApiKey = apiKey, AnswerModel = model! }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiGroundedAnswerGenerator>.Instance);

    private static FakeChatCompletionHandler AnsweredHandler(string content) =>
        new(_ => FakeChatCompletion.Ok(content));

    // ================================================================
    // Argument + configuration guards
    // ================================================================

    [Fact]
    public async Task Null_request_is_rejected()
    {
        var handler = new FakeChatCompletionHandler(_ =>
            throw new InvalidOperationException("transport must not be called"));
        var sut = FakeChatCompletion.Sut(handler);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.GenerateAsync(null!, CancellationToken.None));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Missing_api_key_throws_answer_generation_exception()
    {
        var sut = ProductionSut(apiKey: null);

        var ex = await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));

        Assert.Contains("OpenAI:ApiKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Whitespace_api_key_throws_answer_generation_exception()
    {
        var sut = ProductionSut(apiKey: "   ");

        var ex = await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));

        Assert.Contains("OpenAI:ApiKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Whitespace_answer_model_throws_answer_generation_exception()
    {
        var sut = ProductionSut(apiKey: "sk-test", model: "   ");

        var ex = await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));

        Assert.Contains("OpenAI:AnswerModel", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================
    // Outgoing request construction
    // ================================================================

    [Fact]
    public async Task Request_targets_configured_model()
    {
        var handler = AnsweredHandler(FakeChatCompletion.StructuredContent("answered", "ok", 1));
        var sut = FakeChatCompletion.Sut(handler);

        await sut.GenerateAsync(Request, CancellationToken.None);

        Assert.Single(handler.RequestBodies);
        Assert.Contains("\"model\":\"gpt-4o-mini\"", handler.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_forwards_system_and_user_prompts()
    {
        var handler = AnsweredHandler(FakeChatCompletion.StructuredContent("answered", "ok", 1));
        var sut = FakeChatCompletion.Sut(handler);

        await sut.GenerateAsync(Request, CancellationToken.None);

        var body = handler.RequestBodies[0];
        Assert.Contains("\"role\":\"system\"", body, StringComparison.Ordinal);
        Assert.Contains("SYSTEM_POLICY_MARKER", body, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"user\"", body, StringComparison.Ordinal);
        Assert.Contains("USER_PROMPT_MARKER", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_uses_strict_json_schema_response_format()
    {
        var handler = AnsweredHandler(FakeChatCompletion.StructuredContent("answered", "ok", 1));
        var sut = FakeChatCompletion.Sut(handler);

        await sut.GenerateAsync(Request, CancellationToken.None);

        var body = handler.RequestBodies[0];
        Assert.Contains("\"type\":\"json_schema\"", body, StringComparison.Ordinal);
        Assert.Contains("\"strict\":true", body, StringComparison.Ordinal);
        Assert.Contains("opsflow_grounded_answer_v1", body, StringComparison.Ordinal);
        Assert.Equal(OpenAiGroundedAnswerGenerator.SchemaName, "opsflow_grounded_answer_v1");
    }

    [Fact]
    public async Task Request_sets_temperature_zero_and_token_cap()
    {
        var handler = AnsweredHandler(FakeChatCompletion.StructuredContent("answered", "ok", 1));
        var sut = FakeChatCompletion.Sut(handler);

        await sut.GenerateAsync(Request, CancellationToken.None);

        var body = handler.RequestBodies[0];
        Assert.Contains("\"temperature\":0", body, StringComparison.Ordinal);
        Assert.Contains("\"max_completion_tokens\":800", body, StringComparison.Ordinal);
        Assert.Equal(800, OpenAiGroundedAnswerGenerator.MaxOutputTokens);
    }

    // ================================================================
    // Response parsing — success paths
    // ================================================================

    [Fact]
    public async Task Answered_response_is_parsed()
    {
        var handler = AnsweredHandler(
            FakeChatCompletion.StructuredContent("answered", "The deployment requires approval.", 2, 1));
        var sut = FakeChatCompletion.Sut(handler);

        var output = await sut.GenerateAsync(Request, CancellationToken.None);

        Assert.Equal(GeneratedAnswerStatus.Answered, output.Status);
        Assert.Equal("The deployment requires approval.", output.Answer);
        Assert.Equal([2, 1], output.CitationNumbers);
    }

    [Fact]
    public async Task Insufficient_response_is_parsed()
    {
        var handler = AnsweredHandler(FakeChatCompletion.StructuredContent("insufficient_evidence", null));
        var sut = FakeChatCompletion.Sut(handler);

        var output = await sut.GenerateAsync(Request, CancellationToken.None);

        Assert.Equal(GeneratedAnswerStatus.InsufficientEvidence, output.Status);
        Assert.Null(output.Answer);
        Assert.Empty(output.CitationNumbers);
    }

    // ================================================================
    // Response parsing — fail closed
    // ================================================================

    [Fact]
    public async Task Malformed_json_content_is_rejected()
    {
        var handler = AnsweredHandler("{not valid json");
        var sut = FakeChatCompletion.Sut(handler);

        await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_status_is_rejected()
    {
        var handler = AnsweredHandler("{\"answer\":\"x\",\"citations\":[]}");
        var sut = FakeChatCompletion.Sut(handler);

        await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_answer_property_is_rejected()
    {
        var handler = AnsweredHandler("{\"status\":\"answered\",\"citations\":[1]}");
        var sut = FakeChatCompletion.Sut(handler);

        await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_citations_is_rejected()
    {
        var handler = AnsweredHandler("{\"status\":\"answered\",\"answer\":\"x\"}");
        var sut = FakeChatCompletion.Sut(handler);

        await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task Null_citations_is_rejected()
    {
        var handler = AnsweredHandler("{\"status\":\"answered\",\"answer\":\"x\",\"citations\":null}");
        var sut = FakeChatCompletion.Sut(handler);

        await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task Non_integer_citation_is_rejected()
    {
        var handler = AnsweredHandler("{\"status\":\"answered\",\"answer\":\"x\",\"citations\":[\"a\"]}");
        var sut = FakeChatCompletion.Sut(handler);

        await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_status_is_rejected()
    {
        var handler = AnsweredHandler("{\"status\":\"weird\",\"answer\":null,\"citations\":[]}");
        var sut = FakeChatCompletion.Sut(handler);

        await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task Empty_content_is_rejected()
    {
        var handler = new FakeChatCompletionHandler(_ => FakeChatCompletion.Ok(""));
        var sut = FakeChatCompletion.Sut(handler);

        await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task Null_content_is_rejected()
    {
        var handler = new FakeChatCompletionHandler(_ => FakeChatCompletion.Ok(content: null));
        var sut = FakeChatCompletion.Sut(handler);

        await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task Length_finish_reason_is_rejected()
    {
        var handler = new FakeChatCompletionHandler(_ =>
            FakeChatCompletion.Ok(FakeChatCompletion.StructuredContent("answered", "partial", 1), finishReason: "length"));
        var sut = FakeChatCompletion.Sut(handler);

        await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task Content_filter_finish_reason_is_rejected()
    {
        var handler = new FakeChatCompletionHandler(_ =>
            FakeChatCompletion.Ok(FakeChatCompletion.StructuredContent("answered", "x", 1), finishReason: "content_filter"));
        var sut = FakeChatCompletion.Sut(handler);

        await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task Refusal_is_rejected()
    {
        var handler = new FakeChatCompletionHandler(_ =>
            FakeChatCompletion.Ok(content: null, finishReason: "stop", refusal: "I cannot help with that."));
        var sut = FakeChatCompletion.Sut(handler);

        await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));
    }

    // ================================================================
    // Provider transport failures
    // ================================================================

    [Fact]
    public async Task Provider_401_is_wrapped()
    {
        var handler = new FakeChatCompletionHandler(_ => FakeChatCompletion.Error(HttpStatusCode.Unauthorized));
        var sut = FakeChatCompletion.Sut(handler);

        var ex = await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));

        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Provider_500_is_wrapped_after_retries()
    {
        var handler = new FakeChatCompletionHandler(_ => FakeChatCompletion.Error(HttpStatusCode.InternalServerError));
        var sut = FakeChatCompletion.Sut(handler, maxRetries: 3);

        var ex = await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));

        Assert.NotNull(ex.InnerException);
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task Network_failure_is_wrapped()
    {
        var handler = new FakeChatCompletionHandler(_ =>
            throw new HttpRequestException("simulated socket failure"));
        var sut = FakeChatCompletion.Sut(handler);

        var ex = await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            sut.GenerateAsync(Request, CancellationToken.None));

        Assert.NotNull(ex.InnerException);
    }

    // ================================================================
    // Cancellation
    // ================================================================

    [Fact]
    public async Task Caller_cancellation_propagates_unwrapped()
    {
        var handler = new FakeChatCompletionHandler(_ =>
            throw new InvalidOperationException("transport must not be called after cancellation"));
        var sut = FakeChatCompletion.Sut(handler);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Record.ExceptionAsync(() => sut.GenerateAsync(Request, cts.Token));

        Assert.IsNotType<AnswerGenerationException>(ex);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    // ================================================================
    // Privacy — no prompt/answer text logged
    // ================================================================

    [Fact]
    public async Task Does_not_log_prompt_or_answer_text()
    {
        const string secretSystem = "TOP-SECRET-SYSTEM-POLICY";
        const string secretUser = "TOP-SECRET-USER-EVIDENCE";
        const string secretAnswer = "TOP-SECRET-ANSWER-BODY";
        var logger = new CapturingLogger<OpenAiGroundedAnswerGenerator>();

        var handler = AnsweredHandler(FakeChatCompletion.StructuredContent("answered", secretAnswer, 1));
        var sut = new OpenAiGroundedAnswerGenerator(
            FakeChatCompletion.Client(handler), FakeChatCompletion.Model, logger);

        await sut.GenerateAsync(new GroundedAnswerGenerationRequest(secretSystem, secretUser), CancellationToken.None);

        Assert.NotEmpty(logger.Messages);
        foreach (var message in logger.Messages)
        {
            Assert.DoesNotContain(secretSystem, message, StringComparison.Ordinal);
            Assert.DoesNotContain(secretUser, message, StringComparison.Ordinal);
            Assert.DoesNotContain(secretAnswer, message, StringComparison.Ordinal);
        }
    }
}
