using System.Net;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Documents;
using OpsFlow.Infrastructure.UnitTests.TestSupport;

namespace OpsFlow.Infrastructure.UnitTests.Documents;

public sealed class BedrockRerankInvokerTests
{
    [Fact]
    public void Builds_expected_model_arn()
    {
        Assert.Equal(
            "arn:aws:bedrock:eu-central-1::foundation-model/cohere.rerank-v3-5:0",
            BedrockRerankInvoker.BuildModelArn("eu-central-1", "cohere.rerank-v3-5:0"));
    }

    [Fact]
    public async Task Sends_query_sources_number_of_results_and_arn_without_metadata()
    {
        var handler = new FakeBedrockRerankHandler(_ =>
            FakeBedrock.Ok([(0, 0.9), (1, 0.4)]));
        using var sut = FakeBedrock.Sut(handler);

        await sut.Invoker.RerankAsync("what is the sla?", ["first chunk", "second chunk"], CancellationToken.None);

        Assert.Single(handler.RequestBodies);
        var body = handler.RequestBodies[0];

        // Query mapping.
        Assert.Contains("\"queries\"", body, StringComparison.Ordinal);
        Assert.Contains("\"textQuery\"", body, StringComparison.Ordinal);
        Assert.Contains("what is the sla?", body, StringComparison.Ordinal);
        Assert.Contains("TEXT", body, StringComparison.Ordinal);

        // Source mapping, in order.
        Assert.Contains("\"sources\"", body, StringComparison.Ordinal);
        Assert.Contains("INLINE", body, StringComparison.Ordinal);
        Assert.Contains("first chunk", body, StringComparison.Ordinal);
        Assert.Contains("second chunk", body, StringComparison.Ordinal);
        Assert.True(
            body.IndexOf("first chunk", StringComparison.Ordinal)
                < body.IndexOf("second chunk", StringComparison.Ordinal),
            "candidate text must be sent in candidate order");

        // NumberOfResults equals the candidate count.
        Assert.Contains("\"numberOfResults\":2", body, StringComparison.Ordinal);

        // Exact model ARN.
        Assert.Contains(
            "arn:aws:bedrock:eu-central-1::foundation-model/cohere.rerank-v3-5:0",
            body, StringComparison.Ordinal);

        // No tenant/document metadata leaves OpsFlow.
        string[] forbidden =
        [
            "organizationId", "projectId", "documentId", "documentChunkId",
            "chunkIndex", "startOffset", "endOffset", "originalHybridRank",
            "fileName",
        ];
        foreach (var token in forbidden)
        {
            Assert.DoesNotContain(token, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Maps_provider_index_and_score()
    {
        var handler = new FakeBedrockRerankHandler(_ =>
            FakeBedrock.Ok([(1, 0.95), (0, 0.25)]));
        using var sut = FakeBedrock.Sut(handler);

        var results = await sut.Invoker.RerankAsync("q", ["a", "b"], CancellationToken.None);

        // Bedrock returns relevance scores at float precision; the adapter uses
        // them only for relative ordering, so compare with tolerance.
        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].Index);
        Assert.Equal(0.95, results[0].RelevanceScore, 5);
        Assert.Equal(0, results[1].Index);
        Assert.Equal(0.25, results[1].RelevanceScore, 5);
    }

    [Fact]
    public async Task Missing_relevance_score_is_rejected()
    {
        var handler = new FakeBedrockRerankHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"results\":[{\"index\":0}]}",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        using var sut = FakeBedrock.Sut(handler);

        await Assert.ThrowsAsync<ChunkRerankingException>(() =>
            sut.Invoker.RerankAsync("q", ["a"], CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "AccessDeniedException")]
    [InlineData(HttpStatusCode.BadRequest, "ValidationException")]
    [InlineData(HttpStatusCode.NotFound, "ResourceNotFoundException")]
    [InlineData((HttpStatusCode)429, "ThrottlingException")]
    [InlineData(HttpStatusCode.BadRequest, "ServiceQuotaExceededException")]
    [InlineData(HttpStatusCode.InternalServerError, "InternalServerException")]
    public async Task Aws_service_errors_map_to_reranking_exception_without_leaking_query(
        HttpStatusCode status, string errorType)
    {
        var handler = new FakeBedrockRerankHandler(_ => FakeBedrock.Error(status, errorType));
        using var sut = FakeBedrock.Sut(handler);

        var ex = await Assert.ThrowsAsync<ChunkRerankingException>(() =>
            sut.Invoker.RerankAsync("secret query text", ["secret candidate text"], CancellationToken.None));

        Assert.DoesNotContain("secret query text", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret candidate text", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_error_maps_to_reranking_exception()
    {
        var handler = new ThrowingBedrockHandler(() => new HttpRequestException("no route to host"));
        using var sut = FakeBedrock.Sut(handler);

        await Assert.ThrowsAsync<ChunkRerankingException>(() =>
            sut.Invoker.RerankAsync("q", ["a"], CancellationToken.None));
    }

    [Fact]
    public async Task Provider_local_timeout_maps_to_reranking_exception()
    {
        // Caller token is NOT cancelled; a provider-local TaskCanceledException is
        // a reranker failure, not caller cancellation.
        var handler = new ThrowingBedrockHandler(() => new TaskCanceledException("provider timeout"));
        using var sut = FakeBedrock.Sut(handler);

        await Assert.ThrowsAsync<ChunkRerankingException>(() =>
            sut.Invoker.RerankAsync("q", ["a"], CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_unchanged()
    {
        var handler = new FakeBedrockRerankHandler(_ => FakeBedrock.Ok([(0, 0.5)]));
        using var sut = FakeBedrock.Sut(handler);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.Invoker.RerankAsync("q", ["a"], cts.Token));
    }

    [Fact]
    public async Task Configured_timeout_bounds_async_request_and_fails_as_reranking_exception()
    {
        // The transport never responds; only the configured 1s OpsFlow timeout
        // (enforced via a linked token, since the SDK client timeout does not
        // bound async calls) can end the call. A 5s caller safety token ensures
        // the test cannot hang if the timeout were not enforced.
        var handler = new PendingBedrockHandler();
        using var sut = FakeBedrock.Sut(handler, timeoutSeconds: 1);

        using var callerSafety = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<ChunkRerankingException>(() =>
            sut.Invoker.RerankAsync("q", ["a"], callerSafety.Token));

        // The configured local timeout won — not the 5s caller safety token — so
        // the caller token must not have been cancelled when the failure surfaced.
        Assert.False(callerSafety.IsCancellationRequested);
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
        Assert.Equal(1, handler.CallCount);
    }
}
