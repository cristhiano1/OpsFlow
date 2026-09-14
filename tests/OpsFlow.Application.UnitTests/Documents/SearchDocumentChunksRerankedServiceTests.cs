using OpsFlow.Application.Documents;
using OpsFlow.Application.UnitTests.TestSupport;

namespace OpsFlow.Application.UnitTests.Documents;

public sealed class SearchDocumentChunksRerankedServiceTests
{
    private const string Query = "reranking query";
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();

    // ================================================================
    // Constructor guards
    // ================================================================

    [Fact]
    public void Constructor_rejects_null_hybrid_service()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SearchDocumentChunksRerankedService(null!, new FakeChunkReranker()));
    }

    [Fact]
    public void Constructor_rejects_null_reranker()
    {
        var hybrid = CreateHybrid([]);
        Assert.Throws<ArgumentNullException>(() =>
            new SearchDocumentChunksRerankedService(hybrid, null!));
    }

    // ================================================================
    // Input validation
    // ================================================================

    [Fact]
    public async Task Search_rejects_null_query()
    {
        var harness = CreateHarness([]);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            harness.Service.SearchAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Search_rejects_empty_organization_id()
    {
        var harness = CreateHarness([]);
        var query = MakeQuery(orgId: Guid.Empty);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.SearchAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task TopK_zero_is_rejected()
    {
        var harness = CreateHarness([]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 0), CancellationToken.None));
    }

    [Fact]
    public async Task TopK_negative_is_rejected()
    {
        var harness = CreateHarness([]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: -1), CancellationToken.None));
    }

    [Fact]
    public async Task TopK_above_50_is_rejected()
    {
        var harness = CreateHarness([]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 51), CancellationToken.None));
    }

    // ================================================================
    // Candidate depth policy: effective = min(50, max(20, TopK))
    // ================================================================

    [Theory]
    [InlineData(1, 20)]
    [InlineData(8, 20)]
    [InlineData(20, 20)]
    [InlineData(21, 21)]
    [InlineData(50, 50)]
    public async Task Candidate_depth_follows_policy(int topK, int expectedCandidateCount)
    {
        // 60 available semantic hits so the fused pool always exceeds any depth.
        var harness = CreateHarness(MakeSemanticHits(60));

        await harness.Service.SearchAsync(MakeQuery(topK: topK), CancellationToken.None);

        Assert.NotNull(harness.Reranker.LastRequest);
        Assert.Equal(expectedCandidateCount, harness.Reranker.LastRequest.Candidates.Count);
    }

    [Fact]
    public async Task TopK_in_1_to_50_is_never_rejected_for_candidate_depth()
    {
        var harness = CreateHarness(MakeSemanticHits(60));

        // TopK=50 exceeds the default depth of 20 but must still be accepted.
        var result = await harness.Service.SearchAsync(MakeQuery(topK: 50), CancellationToken.None);

        Assert.True(result.ProjectFound);
        Assert.Equal(50, result.Hits.Count);
    }

    // ================================================================
    // Orchestration
    // ================================================================

    [Fact]
    public async Task Hybrid_is_called_exactly_once()
    {
        var harness = CreateHarness(MakeSemanticHits(5));

        await harness.Service.SearchAsync(MakeQuery(topK: 5), CancellationToken.None);

        // Hybrid retrieval embeds the query exactly once per invocation.
        Assert.Equal(1, harness.Generator.GenerateCallCount);
    }

    [Fact]
    public async Task Reranker_is_called_exactly_once()
    {
        var harness = CreateHarness(MakeSemanticHits(5));

        await harness.Service.SearchAsync(MakeQuery(topK: 5), CancellationToken.None);

        Assert.Equal(1, harness.Reranker.CallCount);
    }

    [Fact]
    public async Task Exact_query_is_forwarded_unchanged_to_hybrid()
    {
        var harness = CreateHarness(MakeSemanticHits(3));
        const string spaced = "  spaced deployment query  ";

        await harness.Service.SearchAsync(MakeQuery(topK: 3, queryText: spaced), CancellationToken.None);

        Assert.NotNull(harness.Generator.ReceivedTexts);
        Assert.Equal(spaced, harness.Generator.ReceivedTexts[0]);
        Assert.Equal(spaced, harness.Lexical.ReceivedQueryText);
    }

    [Fact]
    public async Task Exact_query_is_forwarded_unchanged_to_reranker()
    {
        var harness = CreateHarness(MakeSemanticHits(3));
        const string spaced = "  spaced deployment query  ";

        await harness.Service.SearchAsync(MakeQuery(topK: 3, queryText: spaced), CancellationToken.None);

        Assert.NotNull(harness.Reranker.LastRequest);
        Assert.Equal(spaced, harness.Reranker.LastRequest.Query);
    }

    [Fact]
    public async Task Candidate_text_and_identity_and_rank_are_preserved()
    {
        var hits = MakeSemanticHits(3);
        var harness = CreateHarness(hits);

        await harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None);

        Assert.NotNull(harness.Reranker.LastRequest);
        var candidates = harness.Reranker.LastRequest.Candidates;
        Assert.Equal(3, candidates.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(hits[i].DocumentChunkId, candidates[i].DocumentChunkId);
            Assert.Equal(hits[i].Text, candidates[i].Text);
            Assert.Equal(i + 1, candidates[i].OriginalHybridRank);
        }
    }

    [Fact]
    public async Task Project_not_found_does_not_call_reranker()
    {
        var harness = CreateHarness(MakeSemanticHits(3), projectExists: false);

        var result = await harness.Service.SearchAsync(MakeQuery(), CancellationToken.None);

        Assert.False(result.ProjectFound);
        Assert.Equal(0, harness.Reranker.CallCount);
    }

    [Fact]
    public async Task Empty_hybrid_result_does_not_call_reranker()
    {
        var harness = CreateHarness([]);

        var result = await harness.Service.SearchAsync(MakeQuery(), CancellationToken.None);

        Assert.True(result.ProjectFound);
        Assert.Empty(result.Hits);
        Assert.Equal(0, harness.Reranker.CallCount);
    }

    // ================================================================
    // Untrusted output validation
    // ================================================================

    [Fact]
    public async Task Unknown_score_id_is_rejected()
    {
        var reranker = new FakeChunkReranker
        {
            ScoreSelector = request =>
                [.. request.Candidates.Select((_, i) =>
                    i == 0
                        ? new ChunkRerankScore(Guid.NewGuid(), 1.0)  // unknown id
                        : new ChunkRerankScore(request.Candidates[i].DocumentChunkId, 1.0))],
        };
        var harness = CreateHarness(MakeSemanticHits(3), reranker);

        await Assert.ThrowsAsync<ChunkRerankingValidationException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_score_id_is_rejected()
    {
        var reranker = new FakeChunkReranker
        {
            // Same count as candidates, but the first id is repeated and another omitted.
            ScoreSelector = request =>
                [.. request.Candidates.Select(_ =>
                    new ChunkRerankScore(request.Candidates[0].DocumentChunkId, 1.0))],
        };
        var harness = CreateHarness(MakeSemanticHits(3), reranker);

        await Assert.ThrowsAsync<ChunkRerankingValidationException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None));
    }

    [Fact]
    public async Task Missing_score_is_rejected()
    {
        var reranker = new FakeChunkReranker
        {
            // One fewer score than candidates.
            ScoreSelector = request =>
                [.. request.Candidates.Take(request.Candidates.Count - 1)
                    .Select(c => new ChunkRerankScore(c.DocumentChunkId, 1.0))],
        };
        var harness = CreateHarness(MakeSemanticHits(3), reranker);

        await Assert.ThrowsAsync<ChunkRerankingValidationException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None));
    }

    [Fact]
    public async Task Extra_score_is_rejected()
    {
        var reranker = new FakeChunkReranker
        {
            ScoreSelector = request =>
            {
                var scores = request.Candidates
                    .Select(c => new ChunkRerankScore(c.DocumentChunkId, 1.0))
                    .ToList();
                scores.Add(new ChunkRerankScore(Guid.NewGuid(), 1.0));
                return scores;
            },
        };
        var harness = CreateHarness(MakeSemanticHits(3), reranker);

        await Assert.ThrowsAsync<ChunkRerankingValidationException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task Non_finite_score_is_rejected(double badScore)
    {
        var reranker = new FakeChunkReranker
        {
            ScoreSelector = request =>
                [.. request.Candidates.Select((c, i) =>
                    new ChunkRerankScore(c.DocumentChunkId, i == 0 ? badScore : 1.0))],
        };
        var harness = CreateHarness(MakeSemanticHits(3), reranker);

        await Assert.ThrowsAsync<ChunkRerankingValidationException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None));
    }

    [Fact]
    public async Task Null_score_collection_is_rejected()
    {
        var reranker = new FakeChunkReranker { ScoreSelector = _ => null! };
        var harness = CreateHarness(MakeSemanticHits(3), reranker);

        await Assert.ThrowsAsync<ChunkRerankingValidationException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None));
    }

    // ================================================================
    // Ordering & metadata
    // ================================================================

    [Fact]
    public async Task Results_are_ordered_by_rerank_score_descending()
    {
        var hits = MakeSemanticHits(3);
        // Hybrid order is hits[0],hits[1],hits[2]; give hits[2] the highest score.
        var reranker = new FakeChunkReranker
        {
            ScoreSelector = request => Score(request,
                (hits[0].DocumentChunkId, 0.1),
                (hits[1].DocumentChunkId, 0.9),
                (hits[2].DocumentChunkId, 0.5)),
        };
        var harness = CreateHarness(hits, reranker);

        var result = await harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None);

        Assert.Equal([hits[1].DocumentChunkId, hits[2].DocumentChunkId, hits[0].DocumentChunkId],
            result.Hits.Select(h => h.DocumentChunkId).ToList());
    }

    [Fact]
    public async Task Score_tie_breaks_by_original_hybrid_rank()
    {
        var hits = MakeSemanticHits(3);
        var reranker = new FakeChunkReranker
        {
            // All equal — order must fall back to original hybrid rank ASC.
            ScoreSelector = request =>
                [.. request.Candidates.Select(c => new ChunkRerankScore(c.DocumentChunkId, 1.0))],
        };
        var harness = CreateHarness(hits, reranker);

        var result = await harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None);

        Assert.Equal([1, 2, 3], result.Hits.Select(h => h.OriginalHybridRank).ToList());
        Assert.Equal([hits[0].DocumentChunkId, hits[1].DocumentChunkId, hits[2].DocumentChunkId],
            result.Hits.Select(h => h.DocumentChunkId).ToList());
    }

    [Fact]
    public async Task Provider_response_order_does_not_control_final_order()
    {
        var hits = MakeSemanticHits(3);
        var reranker = new FakeChunkReranker
        {
            // Equal scores, returned in reversed order — final order must still
            // follow original hybrid rank, not the provider's arrival order.
            ScoreSelector = request =>
                [.. request.Candidates.Reverse().Select(c => new ChunkRerankScore(c.DocumentChunkId, 1.0))],
        };
        var harness = CreateHarness(hits, reranker);

        var result = await harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None);

        Assert.Equal([1, 2, 3], result.Hits.Select(h => h.OriginalHybridRank).ToList());
    }

    [Fact]
    public async Task Final_result_is_truncated_to_requested_TopK()
    {
        var harness = CreateHarness(MakeSemanticHits(10));

        var result = await harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None);

        Assert.Equal(3, result.Hits.Count);
    }

    [Fact]
    public async Task Authoritative_metadata_comes_from_hybrid_hit()
    {
        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();
        var semantic = new List<SemanticChunkHit>
        {
            new(documentId, chunkId, 7, 42, 99, "authoritative chunk text", 0.1),
        };
        // Reranker returns only an id + score; it cannot supply metadata.
        var reranker = new FakeChunkReranker
        {
            ScoreSelector = request =>
                [.. request.Candidates.Select(c => new ChunkRerankScore(c.DocumentChunkId, 5.0))],
        };
        var harness = CreateHarness(semantic, reranker);

        var result = await harness.Service.SearchAsync(MakeQuery(topK: 1), CancellationToken.None);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(documentId, hit.DocumentId);
        Assert.Equal(chunkId, hit.DocumentChunkId);
        Assert.Equal(7, hit.ChunkIndex);
        Assert.Equal(42, hit.StartOffset);
        Assert.Equal(99, hit.EndOffset);
        Assert.Equal("authoritative chunk text", hit.Text);
        Assert.Equal(5.0, hit.RerankScore);
        Assert.Equal(1, hit.OriginalHybridRank);
    }

    // ================================================================
    // Reranker identity & capacity
    // ================================================================

    [Theory]
    [InlineData("", "model")]
    [InlineData("   ", "model")]
    [InlineData("profile", "")]
    [InlineData("profile", "   ")]
    public async Task Invalid_reranker_identity_is_rejected(string profileId, string modelId)
    {
        var reranker = new FakeChunkReranker { Identity = new RerankerIdentity(profileId, modelId, 50) };
        var harness = CreateHarness(MakeSemanticHits(3), reranker);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None));

        // Identity is validated before any retrieval or reranking.
        Assert.Equal(0, harness.Generator.GenerateCallCount);
        Assert.Equal(0, harness.Reranker.CallCount);
    }

    [Fact]
    public async Task Reranker_identity_with_non_positive_max_candidates_is_rejected()
    {
        var reranker = new FakeChunkReranker { Identity = new RerankerIdentity("profile", "model", 0) };
        var harness = CreateHarness(MakeSemanticHits(3), reranker);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None));
    }

    [Fact]
    public async Task Candidate_count_above_provider_max_is_rejected()
    {
        var reranker = new FakeChunkReranker { Identity = new RerankerIdentity("profile", "model", 2) };
        // 5 candidates available; provider capacity is 2.
        var harness = CreateHarness(MakeSemanticHits(5), reranker);

        await Assert.ThrowsAsync<ChunkRerankingException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 5), CancellationToken.None));

        Assert.Equal(0, harness.Reranker.CallCount);
    }

    [Fact]
    public async Task Duplicate_hybrid_chunk_identity_is_rejected()
    {
        var documentId = Guid.NewGuid();
        var sharedChunkId = Guid.NewGuid();
        // Two semantic hits with the SAME chunk id: RRF fails fast on ambiguous
        // identity, so the wrapper never sends it to the reranker.
        var semantic = new List<SemanticChunkHit>
        {
            new(documentId, sharedChunkId, 0, 0, 5, "one", 0.1),
            new(documentId, sharedChunkId, 1, 5, 10, "two", 0.2),
        };
        var harness = CreateHarness(semantic);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 5), CancellationToken.None));

        Assert.Equal(0, harness.Reranker.CallCount);
    }

    // ================================================================
    // Cancellation
    // ================================================================

    [Fact]
    public async Task Cancellation_from_hybrid_propagates_and_reranker_is_not_called()
    {
        var generator = new RecordingEmbeddingGenerator { ThrowCanceled = true };
        var harness = CreateHarness(MakeSemanticHits(3), generator: generator);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None));

        Assert.Equal(0, harness.Reranker.CallCount);
    }

    [Fact]
    public async Task Cancellation_from_reranker_propagates_unwrapped()
    {
        var reranker = new FakeChunkReranker { ExceptionToThrow = new OperationCanceledException() };
        var harness = CreateHarness(MakeSemanticHits(3), reranker);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None));
    }

    [Fact]
    public async Task Generic_reranker_failure_is_wrapped_as_reranking_exception()
    {
        var reranker = new FakeChunkReranker { ExceptionToThrow = new InvalidOperationException("boom") };
        var harness = CreateHarness(MakeSemanticHits(3), reranker);

        await Assert.ThrowsAsync<ChunkRerankingException>(() =>
            harness.Service.SearchAsync(MakeQuery(topK: 3), CancellationToken.None));
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static SearchDocumentChunksRerankedQuery MakeQuery(
        int topK = 5,
        string queryText = Query,
        Guid? orgId = null,
        Guid? projectId = null) =>
        new(orgId ?? OrgId, projectId ?? ProjectId, queryText, topK);

    private static List<SemanticChunkHit> MakeSemanticHits(int count)
    {
        var list = new List<SemanticChunkHit>(count);
        for (int i = 0; i < count; i++)
        {
            // Strictly increasing cosine distance keeps the fused (RRF) order
            // equal to input order; distinct chunk ids for unambiguous identity.
            list.Add(new SemanticChunkHit(
                Guid.NewGuid(), Guid.NewGuid(), i, i * 5, (i * 5) + 5, $"chunk text {i}", 0.01 * (i + 1)));
        }

        return list;
    }

    private static IReadOnlyList<ChunkRerankScore> Score(
        ChunkRerankRequest request,
        params (Guid ChunkId, double Score)[] scores)
    {
        var map = scores.ToDictionary(s => s.ChunkId, s => s.Score);
        return [.. request.Candidates.Select(c => new ChunkRerankScore(c.DocumentChunkId, map[c.DocumentChunkId]))];
    }

    private static SearchDocumentChunksHybridService CreateHybrid(
        IReadOnlyList<SemanticChunkHit> semanticHits) =>
        new(
            new FakeProjectRepository { ExistsResult = true },
            new RecordingEmbeddingGenerator(),
            new FakeSemanticChunkRetriever { RetrieveResult = semanticHits },
            new FakeLexicalChunkRetriever());

    private static RerankHarness CreateHarness(
        IReadOnlyList<SemanticChunkHit> semanticHits,
        FakeChunkReranker? reranker = null,
        bool projectExists = true,
        RecordingEmbeddingGenerator? generator = null,
        IReadOnlyList<LexicalChunkHit>? lexicalHits = null)
    {
        var projects = new FakeProjectRepository { ExistsResult = projectExists };
        var gen = generator ?? new RecordingEmbeddingGenerator();
        var semantic = new FakeSemanticChunkRetriever { RetrieveResult = semanticHits };
        var lexical = new FakeLexicalChunkRetriever { RetrieveResult = lexicalHits ?? [] };
        var hybrid = new SearchDocumentChunksHybridService(projects, gen, semantic, lexical);
        var rr = reranker ?? new FakeChunkReranker();
        var service = new SearchDocumentChunksRerankedService(hybrid, rr);
        return new RerankHarness(service, gen, lexical, rr);
    }

    private sealed record RerankHarness(
        SearchDocumentChunksRerankedService Service,
        RecordingEmbeddingGenerator Generator,
        FakeLexicalChunkRetriever Lexical,
        FakeChunkReranker Reranker);

    private sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator
    {
        public EmbeddingGeneratorIdentity Identity { get; } = new(
            EmbeddingProfiles.SemanticV1Id,
            EmbeddingProfiles.SemanticV1ModelId,
            EmbeddingProfiles.SemanticV1Dimensions);

        public int GenerateCallCount { get; private set; }
        public IReadOnlyList<string>? ReceivedTexts { get; private set; }
        public bool ThrowCanceled { get; set; }

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken)
        {
            GenerateCallCount++;
            ReceivedTexts = texts;

            if (ThrowCanceled)
            {
                throw new OperationCanceledException();
            }

            var vector = new float[EmbeddingProfiles.SemanticV1Dimensions];
            vector[0] = 1.0f;
            IReadOnlyList<ReadOnlyMemory<float>> result =
                [.. texts.Select(_ => (ReadOnlyMemory<float>)vector)];
            return Task.FromResult(result);
        }
    }

    private sealed class FakeChunkReranker : IChunkReranker
    {
        public RerankerIdentity Identity { get; set; } = new("test-rerank-v1", "test-model", 50);

        public int CallCount { get; private set; }
        public ChunkRerankRequest? LastRequest { get; private set; }
        public Exception? ExceptionToThrow { get; set; }
        public Func<ChunkRerankRequest, IReadOnlyList<ChunkRerankScore>>? ScoreSelector { get; set; }

        public Task<IReadOnlyList<ChunkRerankScore>> RerankAsync(
            ChunkRerankRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;

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
}
