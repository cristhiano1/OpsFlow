using System.Net;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Documents;
using OpsFlow.Infrastructure.UnitTests.TestSupport;

namespace OpsFlow.Infrastructure.UnitTests.Documents;

public sealed class OpenAIEmbeddingGeneratorTests
{
    // A handler that echoes one vector per input, whose single element is the
    // input's global position. Lets tests assert cross-batch order preservation.
    private static FakeEmbeddingHandler SequentialHandler()
    {
        var offset = 0;
        FakeEmbeddingHandler handler = null!;
        handler = new FakeEmbeddingHandler(inputs =>
        {
            var entries = new (int Index, float[] Vector)[inputs.Count];
            for (var i = 0; i < inputs.Count; i++)
            {
                entries[i] = (i, [offset + i]);
            }
            offset += inputs.Count;
            return FakeEmbedding.Ok(entries);
        });
        return handler;
    }

    private static List<string> Inputs(int count)
    {
        var list = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add($"input-{i}");
        }
        return list;
    }

    private static OpenAIEmbeddingGenerator ProductionSut(string? apiKey) =>
        new(Options.Create(new OpenAIEmbeddingOptions { ApiKey = apiKey }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAIEmbeddingGenerator>.Instance);

    // ================================================================
    // 1–3. Identity
    // ================================================================

    [Fact]
    public void Identity_profile_id_is_semantic_v1()
    {
        var sut = ProductionSut(apiKey: null);
        Assert.Equal(EmbeddingProfiles.SemanticV1Id, sut.Identity.ProfileId);
    }

    [Fact]
    public void Identity_model_id_is_text_embedding_3_small()
    {
        var sut = ProductionSut(apiKey: null);
        Assert.Equal("text-embedding-3-small", sut.Identity.ModelId);
    }

    [Fact]
    public void Identity_dimensions_is_1536()
    {
        var sut = ProductionSut(apiKey: null);
        Assert.Equal(EmbeddingProfiles.SemanticV1Dimensions, sut.Identity.Dimensions);
        Assert.Equal(1536, sut.Identity.Dimensions);
    }

    // ================================================================
    // 4. Empty input short-circuits (no transport call)
    // ================================================================

    [Fact]
    public async Task Empty_input_returns_empty_without_transport_call()
    {
        var handler = new FakeEmbeddingHandler(_ =>
            throw new InvalidOperationException("transport must not be called for empty input"));
        var sut = FakeEmbedding.Sut(handler);

        var result = await sut.GenerateAsync([], CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(0, handler.CallCount);
    }

    // ================================================================
    // 5. Missing API key throws provider-neutral exception
    // ================================================================

    [Fact]
    public async Task Without_api_key_throws_embedding_generation_exception()
    {
        var sut = ProductionSut(apiKey: null);

        var ex = await Assert.ThrowsAsync<EmbeddingGenerationException>(() =>
            sut.GenerateAsync(["hello"], CancellationToken.None));

        Assert.Contains("OpenAI:ApiKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Whitespace_api_key_throws_embedding_generation_exception()
    {
        var sut = ProductionSut(apiKey: "   ");

        await Assert.ThrowsAsync<EmbeddingGenerationException>(() =>
            sut.GenerateAsync(["hello"], CancellationToken.None));
    }

    // ================================================================
    // 6. Single batch preserves input order
    // ================================================================

    [Fact]
    public async Task Single_batch_preserves_input_order()
    {
        var handler = SequentialHandler();
        var sut = FakeEmbedding.Sut(handler);

        var result = await sut.GenerateAsync(Inputs(5), CancellationToken.None);

        Assert.Equal(5, result.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(i, result[i].Span[0]);
        }
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Single_batch_reorders_by_response_index()
    {
        // Response lists embeddings in reverse index order; the SUT must
        // reconstruct ascending order using the reported index.
        var handler = new FakeEmbeddingHandler(inputs =>
        {
            var entries = new List<(int Index, float[] Vector)>(inputs.Count);
            for (var i = inputs.Count - 1; i >= 0; i--)
            {
                entries.Add((i, [i]));
            }
            return FakeEmbedding.Ok(entries);
        });
        var sut = FakeEmbedding.Sut(handler);

        var result = await sut.GenerateAsync(Inputs(4), CancellationToken.None);

        Assert.Equal(4, result.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(i, result[i].Span[0]);
        }
    }

    // ================================================================
    // 7–9. Batching boundaries and global order
    // ================================================================

    [Fact]
    public async Task Sixty_inputs_produce_one_request()
    {
        var handler = SequentialHandler();
        var sut = FakeEmbedding.Sut(handler);

        var result = await sut.GenerateAsync(Inputs(60), CancellationToken.None);

        Assert.Equal(60, result.Count);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SixtyOne_inputs_produce_two_requests()
    {
        var handler = SequentialHandler();
        var sut = FakeEmbedding.Sut(handler);

        var result = await sut.GenerateAsync(Inputs(61), CancellationToken.None);

        Assert.Equal(61, result.Count);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Multiple_batches_preserve_global_order()
    {
        var handler = SequentialHandler();
        var sut = FakeEmbedding.Sut(handler);

        var result = await sut.GenerateAsync(Inputs(150), CancellationToken.None);

        Assert.Equal(150, result.Count);
        Assert.Equal(3, handler.CallCount);
        for (var i = 0; i < 150; i++)
        {
            Assert.Equal(i, result[i].Span[0]);
        }
    }

    [Fact]
    public async Task First_batch_is_full_sixty_on_boundary_split()
    {
        var handler = SequentialHandler();
        var sut = FakeEmbedding.Sut(handler);

        await sut.GenerateAsync(Inputs(61), CancellationToken.None);

        // First request carries 60 inputs, second carries 1.
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Equal(60, CountInputs(handler.RequestBodies[0]));
        Assert.Equal(1, CountInputs(handler.RequestBodies[1]));
    }

    // ================================================================
    // 10. Request carries the fixed dimension
    // ================================================================

    [Fact]
    public async Task Request_includes_dimensions_1536()
    {
        var handler = SequentialHandler();
        var sut = FakeEmbedding.Sut(handler);

        await sut.GenerateAsync(Inputs(2), CancellationToken.None);

        Assert.Single(handler.RequestBodies);
        Assert.Contains("\"dimensions\":1536", handler.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_targets_the_fixed_model()
    {
        var handler = SequentialHandler();
        var sut = FakeEmbedding.Sut(handler);

        await sut.GenerateAsync(Inputs(1), CancellationToken.None);

        Assert.Contains("\"model\":\"text-embedding-3-small\"", handler.RequestBodies[0], StringComparison.Ordinal);
    }

    // ================================================================
    // 11–12. Malformed provider responses
    // ================================================================

    [Fact]
    public async Task Wrong_response_count_throws()
    {
        // Two inputs but only one embedding returned.
        var handler = new FakeEmbeddingHandler(_ =>
            FakeEmbedding.Ok([(0, [1f])]));
        var sut = FakeEmbedding.Sut(handler);

        var ex = await Assert.ThrowsAsync<EmbeddingGenerationException>(() =>
            sut.GenerateAsync(Inputs(2), CancellationToken.None));

        Assert.Contains("expected 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Out_of_range_index_throws()
    {
        // One input, but the embedding claims index 5.
        var handler = new FakeEmbeddingHandler(_ =>
            FakeEmbedding.Ok([(5, [1f])]));
        var sut = FakeEmbedding.Sut(handler);

        await Assert.ThrowsAsync<EmbeddingGenerationException>(() =>
            sut.GenerateAsync(Inputs(1), CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_index_throws()
    {
        // Two inputs, but both embeddings claim index 0.
        var handler = new FakeEmbeddingHandler(_ =>
            FakeEmbedding.Ok([(0, [1f]), (0, [2f])]));
        var sut = FakeEmbedding.Sut(handler);

        var ex = await Assert.ThrowsAsync<EmbeddingGenerationException>(() =>
            sut.GenerateAsync(Inputs(2), CancellationToken.None));

        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    // 13. Caller cancellation propagates unwrapped
    // ================================================================

    [Fact]
    public async Task Caller_cancellation_propagates_unwrapped()
    {
        var handler = new FakeEmbeddingHandler(_ =>
            throw new InvalidOperationException("transport must not be called after cancellation"));
        var sut = FakeEmbedding.Sut(handler);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.GenerateAsync(Inputs(3), cts.Token));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped_as_generation_exception()
    {
        var handler = SequentialHandler();
        var sut = FakeEmbedding.Sut(handler);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Record.ExceptionAsync(() =>
            sut.GenerateAsync(Inputs(3), cts.Token));

        Assert.IsNotType<EmbeddingGenerationException>(ex);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    // ================================================================
    // 14–16. Provider HTTP failures are wrapped
    // ================================================================

    [Fact]
    public async Task Provider_401_is_wrapped()
    {
        var handler = new FakeEmbeddingHandler(_ => FakeEmbedding.Error(HttpStatusCode.Unauthorized));
        var sut = FakeEmbedding.Sut(handler);

        var ex = await Assert.ThrowsAsync<EmbeddingGenerationException>(() =>
            sut.GenerateAsync(Inputs(1), CancellationToken.None));

        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Provider_401_is_not_retried()
    {
        var handler = new FakeEmbeddingHandler(_ => FakeEmbedding.Error(HttpStatusCode.Unauthorized));
        var sut = FakeEmbedding.Sut(handler);

        await Assert.ThrowsAsync<EmbeddingGenerationException>(() =>
            sut.GenerateAsync(Inputs(1), CancellationToken.None));

        // 401 is a terminal auth failure — the SDK does not retry it.
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Provider_429_is_wrapped_after_retries()
    {
        var handler = new FakeEmbeddingHandler(_ => FakeEmbedding.Error(HttpStatusCode.TooManyRequests));
        var sut = FakeEmbedding.Sut(handler, maxRetries: 3);

        var ex = await Assert.ThrowsAsync<EmbeddingGenerationException>(() =>
            sut.GenerateAsync(Inputs(1), CancellationToken.None));

        Assert.NotNull(ex.InnerException);
        // 429 is retryable: one initial attempt plus three retries.
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task Provider_500_is_wrapped_after_retries()
    {
        var handler = new FakeEmbeddingHandler(_ => FakeEmbedding.Error(HttpStatusCode.InternalServerError));
        var sut = FakeEmbedding.Sut(handler, maxRetries: 3);

        var ex = await Assert.ThrowsAsync<EmbeddingGenerationException>(() =>
            sut.GenerateAsync(Inputs(1), CancellationToken.None));

        Assert.NotNull(ex.InnerException);
        Assert.Equal(4, handler.CallCount);
    }

    // ================================================================
    // 17. Network failure (caller token not cancelled) is wrapped
    // ================================================================

    [Fact]
    public async Task Network_failure_is_wrapped_when_caller_token_not_cancelled()
    {
        var handler = new FakeEmbeddingHandler(_ =>
            throw new HttpRequestException("simulated socket failure"));
        var sut = FakeEmbedding.Sut(handler);

        var ex = await Assert.ThrowsAsync<EmbeddingGenerationException>(() =>
            sut.GenerateAsync(Inputs(1), CancellationToken.None));

        Assert.NotNull(ex.InnerException);
    }

    // ================================================================
    // 18. No input text or vector payload is logged
    // ================================================================

    [Fact]
    public async Task Does_not_log_input_text_or_vector_payload()
    {
        const string secret = "TOP-SECRET-DOCUMENT-BODY";
        var logger = new CapturingLogger<OpenAIEmbeddingGenerator>();

        var handler = new FakeEmbeddingHandler(_ =>
            FakeEmbedding.Ok([(0, [0.123456f, 0.654321f])]));
        var sut = new OpenAIEmbeddingGenerator(FakeEmbedding.Client(handler), logger);

        await sut.GenerateAsync([secret], CancellationToken.None);

        Assert.NotEmpty(logger.Messages);
        foreach (var message in logger.Messages)
        {
            Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
            Assert.DoesNotContain("0.123456", message, StringComparison.Ordinal);
        }
    }

    private static int CountInputs(string requestBody)
    {
        using var document = System.Text.Json.JsonDocument.Parse(requestBody);
        return document.RootElement.GetProperty("input").GetArrayLength();
    }
}
