using Microsoft.Extensions.Options;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Infrastructure.UnitTests.Documents;

public sealed class BedrockChunkRerankerTests
{
    private static IOptions<BedrockRerankerOptions> Options(string modelId = "cohere.rerank-v3-5:0") =>
        Microsoft.Extensions.Options.Options.Create(new BedrockRerankerOptions
        {
            Region = "eu-central-1",
            ModelId = modelId,
            TimeoutSeconds = 30,
        });

    private static ChunkRerankRequest Request(params (Guid Id, string Text)[] candidates)
    {
        var list = new List<ChunkRerankCandidate>(candidates.Length);
        for (int i = 0; i < candidates.Length; i++)
        {
            list.Add(new ChunkRerankCandidate(candidates[i].Id, i + 1, candidates[i].Text));
        }

        return new ChunkRerankRequest("what is the sla?", list);
    }

    private sealed class FakeInvoker : IBedrockRerankInvoker
    {
        private readonly Func<string, IReadOnlyList<string>, IReadOnlyList<BedrockRerankResult>> _respond;

        public FakeInvoker(Func<string, IReadOnlyList<string>, IReadOnlyList<BedrockRerankResult>> respond)
        {
            _respond = respond;
        }

        public int Calls { get; private set; }

        public string? LastQuery { get; private set; }

        public IReadOnlyList<string>? LastDocuments { get; private set; }

        public Task<IReadOnlyList<BedrockRerankResult>> RerankAsync(
            string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
        {
            Calls++;
            LastQuery = query;
            LastDocuments = documents;
            return Task.FromResult(_respond(query, documents));
        }
    }

    [Fact]
    public void Identity_reflects_profile_model_and_operational_capacity()
    {
        var sut = new BedrockChunkReranker(
            new FakeInvoker((_, d) => []), Options("amazon.rerank-v1:0"));

        Assert.Equal("opsflow-rerank-v1", sut.Identity.ProfileId);
        Assert.Equal("amazon.rerank-v1:0", sut.Identity.ModelId);
        Assert.Equal(100, sut.Identity.MaxCandidates);
    }

    [Fact]
    public async Task Forwards_exact_query_and_preserves_candidate_text_order()
    {
        var invoker = new FakeInvoker((_, d) => [.. d.Select((_, i) => new BedrockRerankResult(i, 1.0 - i))]);
        var sut = new BedrockChunkReranker(invoker, Options());

        var request = Request(
            (Guid.NewGuid(), "alpha text"),
            (Guid.NewGuid(), "beta text"),
            (Guid.NewGuid(), "gamma text"));

        await sut.RerankAsync(request, CancellationToken.None);

        string[] expectedDocuments = ["alpha text", "beta text", "gamma text"];
        Assert.Equal("what is the sla?", invoker.LastQuery);
        Assert.Equal(expectedDocuments, invoker.LastDocuments);
    }

    [Fact]
    public async Task Maps_result_index_to_correct_document_chunk_id()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var invoker = new FakeInvoker((_, _) =>
        [
            new BedrockRerankResult(0, 0.1),
            new BedrockRerankResult(1, 0.9),
            new BedrockRerankResult(2, 0.5),
        ]);
        var sut = new BedrockChunkReranker(invoker, Options());

        var scores = await sut.RerankAsync(Request((a, "a"), (b, "b"), (c, "c")), CancellationToken.None);

        Assert.Equal(3, scores.Count);
        Assert.Equal(a, scores[0].DocumentChunkId);
        Assert.Equal(0.1, scores[0].RelevanceScore);
        Assert.Equal(b, scores[1].DocumentChunkId);
        Assert.Equal(0.9, scores[1].RelevanceScore);
        Assert.Equal(c, scores[2].DocumentChunkId);
        Assert.Equal(0.5, scores[2].RelevanceScore);
    }

    [Fact]
    public async Task Correlates_correctly_when_provider_returns_results_out_of_order()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        // Provider returns results in a different order than the request.
        var invoker = new FakeInvoker((_, _) =>
        [
            new BedrockRerankResult(2, 0.7),
            new BedrockRerankResult(0, 0.2),
            new BedrockRerankResult(1, 0.95),
        ]);
        var sut = new BedrockChunkReranker(invoker, Options());

        var scores = await sut.RerankAsync(Request((a, "a"), (b, "b"), (c, "c")), CancellationToken.None);

        // Order is preserved as returned (Application reorders); correlation is by index.
        Assert.Equal(c, scores[0].DocumentChunkId);
        Assert.Equal(0.7, scores[0].RelevanceScore);
        Assert.Equal(a, scores[1].DocumentChunkId);
        Assert.Equal(b, scores[2].DocumentChunkId);
        Assert.Equal(0.95, scores[2].RelevanceScore);
    }

    [Fact]
    public async Task Rejects_negative_result_index_as_validation()
    {
        // An un-correlatable index is malformed/untrusted output, not an
        // operational failure: it must fail closed (never a fallback).
        var invoker = new FakeInvoker((_, _) => [new BedrockRerankResult(-1, 0.5)]);
        var sut = new BedrockChunkReranker(invoker, Options());

        await Assert.ThrowsAsync<ChunkRerankingValidationException>(() =>
            sut.RerankAsync(Request((Guid.NewGuid(), "a")), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_upper_out_of_range_result_index_as_validation()
    {
        var invoker = new FakeInvoker((_, _) => [new BedrockRerankResult(5, 0.5)]);
        var sut = new BedrockChunkReranker(invoker, Options());

        await Assert.ThrowsAsync<ChunkRerankingValidationException>(() =>
            sut.RerankAsync(Request((Guid.NewGuid(), "a"), (Guid.NewGuid(), "b")), CancellationToken.None));
    }

    [Fact]
    public async Task Propagates_reranking_exception_from_invoker_unchanged()
    {
        var invoker = new FakeInvoker((_, _) => throw new ChunkRerankingException("provider unavailable"));
        var sut = new BedrockChunkReranker(invoker, Options());

        var ex = await Assert.ThrowsAsync<ChunkRerankingException>(() =>
            sut.RerankAsync(Request((Guid.NewGuid(), "a")), CancellationToken.None));
        Assert.Equal("provider unavailable", ex.Message);
    }

    [Fact]
    public async Task Propagates_caller_cancellation()
    {
        var invoker = new FakeInvoker((_, _) => throw new OperationCanceledException());
        var sut = new BedrockChunkReranker(invoker, Options());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.RerankAsync(Request((Guid.NewGuid(), "a")), CancellationToken.None));
    }

    [Fact]
    public async Task Empty_candidates_returns_empty_without_calling_provider()
    {
        var invoker = new FakeInvoker((_, _) => throw new InvalidOperationException("must not be called"));
        var sut = new BedrockChunkReranker(invoker, Options());

        var scores = await sut.RerankAsync(new ChunkRerankRequest("q", []), CancellationToken.None);

        Assert.Empty(scores);
        Assert.Equal(0, invoker.Calls);
    }

    [Fact]
    public async Task Returns_every_score_without_truncation_at_capacity_boundary()
    {
        var candidates = new (Guid, string)[100];
        for (int i = 0; i < 100; i++)
        {
            candidates[i] = (Guid.NewGuid(), $"chunk-{i}");
        }

        var invoker = new FakeInvoker((_, d) => [.. d.Select((_, i) => new BedrockRerankResult(i, i))]);
        var sut = new BedrockChunkReranker(invoker, Options());

        var scores = await sut.RerankAsync(Request(candidates), CancellationToken.None);

        Assert.Equal(100, scores.Count);
        Assert.Equal(100, sut.Identity.MaxCandidates);
    }
}
