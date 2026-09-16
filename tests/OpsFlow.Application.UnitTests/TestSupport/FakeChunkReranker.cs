using OpsFlow.Application.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

/// <summary>
/// Configurable fake <see cref="IChunkReranker"/> for Application tests. Makes no
/// network call. Lets a test script the returned scores, throw a chosen
/// exception, observe the request, and mutate ambient state at invocation time.
/// </summary>
internal sealed class FakeChunkReranker : IChunkReranker
{
    public RerankerIdentity Identity { get; set; } = new("test-rerank-v1", "test-model", 50);

    public int CallCount { get; private set; }

    public ChunkRerankRequest? LastRequest { get; private set; }

    /// <summary>When set, thrown instead of scoring.</summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>When set, produces the scores; otherwise hybrid order is preserved.</summary>
    public Func<ChunkRerankRequest, IReadOnlyList<ChunkRerankScore>>? ScoreSelector { get; set; }

    /// <summary>Optional hook run when invoked, before it throws or scores.</summary>
    public Action? OnRerank { get; set; }

    public Task<IReadOnlyList<ChunkRerankScore>> RerankAsync(
        ChunkRerankRequest request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;

        OnRerank?.Invoke();

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        if (ScoreSelector is not null)
        {
            return Task.FromResult(ScoreSelector(request));
        }

        // Default: preserve hybrid order (first candidate scores highest).
        IReadOnlyList<ChunkRerankScore> result =
            [.. request.Candidates.Select((c, i) =>
                new ChunkRerankScore(c.DocumentChunkId, request.Candidates.Count - i))];
        return Task.FromResult(result);
    }
}
