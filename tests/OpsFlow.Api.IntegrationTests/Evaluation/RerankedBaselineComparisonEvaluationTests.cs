using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpsFlow.Api.IntegrationTests.Authentication;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;
using OpsFlow.Domain.Organizations;
using OpsFlow.Domain.Projects;
using OpsFlow.Evaluation.Dataset;
using OpsFlow.Evaluation.Retrieval;
using OpsFlow.Infrastructure.Documents;
using Xunit.Abstractions;

namespace OpsFlow.Api.IntegrationTests.Evaluation;

/// <summary>
/// SQL-backed baseline-vs-candidate retrieval comparison. Runs the baseline
/// hybrid retrieval and the reranked path separately against the real pipeline
/// (real semantic vector retrieval, real full-text retrieval, real RRF) at a
/// candidate depth of 20, then proves the reranker's captured candidate request
/// is element-for-element identical to the baseline RRF pool before comparing
/// their rankings on the same dataset/tenant/query/K values: baseline (RRF order)
/// versus candidate (the real
/// <see cref="SearchDocumentChunksRerankedService"/> driven by a deterministic
/// test reranker). Only the embedding provider is replaced.
///
/// <para>
/// The comparison is REPORT-ONLY and asserts architectural invariants only
/// (tenant isolation, no duplicate/foreign ids, pool bounds, well-formed
/// metrics). The deterministic token-overlap reranker proves plumbing, not
/// real-world reranking quality (see ADR-009), so the test never asserts that
/// the candidate beats the baseline.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RerankedBaselineComparisonEvaluationTests : IDisposable
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);
    private const int CandidateDepth = 20;

    private readonly SqlServerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly DeterministicContentEmbeddingGenerator _embedding = new();
    private readonly OpsFlowWebApplicationFactory _baseFactory;
    private readonly WebApplicationFactory<Program> _factory;

    public RerankedBaselineComparisonEvaluationTests(SqlServerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _baseFactory = new OpsFlowWebApplicationFactory(fixture.ConnectionString);
        _factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmbeddingGenerator>();
                services.AddSingleton<IEmbeddingGenerator>(_embedding);
            });
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    [Fact]
    public async Task Reranked_vs_baseline_comparison_reports_metrics_and_holds_invariants()
    {
        const string scenario = "rerank-baseline";
        var dataset = EvaluationDatasetLoader.LoadSyntheticV1();
        var organizationId = StableGuid($"{scenario}:org-a");
        var projectId = StableGuid($"{scenario}:project-a");

        var chunkKeyById = await SeedCorpusAsync(scenario, organizationId, projectId, dataset);
        await WaitForFullTextPopulationAsync();

        var baselineByCaseId = new Dictionary<string, IReadOnlyList<RetrievalEvaluationHit>>(StringComparer.Ordinal);
        var candidateByCaseId = new Dictionary<string, IReadOnlyList<RetrievalEvaluationHit>>(StringComparer.Ordinal);

        using (var scope = _factory.Services.CreateScope())
        {
            var hybrid = scope.ServiceProvider.GetRequiredService<SearchDocumentChunksHybridService>();

            foreach (var evaluationCase in dataset.Cases)
            {
                // Baseline: the RRF-ordered candidate pool at depth 20.
                var baseline = await hybrid.SearchAsync(
                    new SearchDocumentChunksHybridQuery(organizationId, projectId, evaluationCase.Query, CandidateDepth),
                    CancellationToken.None);
                Assert.True(baseline.ProjectFound);
                Assert.True(baseline.Hits.Count <= CandidateDepth);

                // A fresh recorder per case captures the exact request the reranker
                // received. The reranked service performs its own hybrid retrieval;
                // we prove — rather than assume — that it reranks the identical RRF
                // pool by asserting element-for-element equality below.
                var recorder = new RecordingChunkReranker(new DeterministicTokenOverlapReranker());
                var reranked = new SearchDocumentChunksRerankedService(hybrid, recorder);

                var candidate = await reranked.SearchAsync(
                    new SearchDocumentChunksRerankedQuery(organizationId, projectId, evaluationCase.Query, CandidateDepth),
                    CancellationToken.None);
                Assert.True(candidate.ProjectFound);
                Assert.True(candidate.Hits.Count <= CandidateDepth);

                // Fair-comparison invariant: prove the candidate reranked exactly
                // the baseline pool BEFORE trusting any metric comparison.
                AssertRerankerSawBaselinePool(evaluationCase, baseline.Hits, recorder);

                baselineByCaseId[evaluationCase.CaseId] =
                    ToHits(evaluationCase.CaseId, baseline.Hits.Select(h => h.DocumentChunkId), chunkKeyById);
                candidateByCaseId[evaluationCase.CaseId] =
                    ToHits(evaluationCase.CaseId, candidate.Hits.Select(h => h.DocumentChunkId), chunkKeyById);
            }
        }

        var baselineResult = RetrievalEvaluator.Evaluate(dataset, baselineByCaseId);
        var candidateResult = RetrievalEvaluator.Evaluate(dataset, candidateByCaseId);

        Assert.Equal(dataset.Cases.Count, baselineResult.CaseCount);
        Assert.Equal(dataset.Cases.Count, candidateResult.CaseCount);

        foreach (var aggregate in baselineResult.Aggregates.Concat(candidateResult.Aggregates))
        {
            Assert.InRange(aggregate.MeanRecall, 0.0, 1.0);
            Assert.InRange(aggregate.MeanReciprocalRank, 0.0, 1.0);
            Assert.InRange(aggregate.MeanNdcg, 0.0, 1.0);
        }

        _output.WriteLine(FormatComparison(dataset, baselineResult, candidateResult));
    }

    [Fact]
    public async Task Reranking_never_returns_foreign_tenant_chunks()
    {
        const string scenario = "rerank-isolation";
        var dataset = EvaluationDatasetLoader.LoadSyntheticV1();

        var organizationA = StableGuid($"{scenario}:org-a");
        var projectA = StableGuid($"{scenario}:project-a");
        var chunkKeyById = await SeedCorpusAsync(scenario, organizationA, projectA, dataset);

        var organizationB = StableGuid($"{scenario}:org-b");
        var projectB = StableGuid($"{scenario}:project-b");
        await SeedForeignDistractorAsync(
            scenario,
            organizationB,
            projectB,
            "If a production deployment fails its health checks, roll it back by redeploying " +
            "the previous known-good release tag and invalidating the CDN cache.");

        await WaitForFullTextPopulationAsync();

        using var scope = _factory.Services.CreateScope();
        var hybrid = scope.ServiceProvider.GetRequiredService<SearchDocumentChunksHybridService>();
        var reranked = new SearchDocumentChunksRerankedService(hybrid, new DeterministicTokenOverlapReranker());

        foreach (var evaluationCase in dataset.Cases)
        {
            var candidate = await reranked.SearchAsync(
                new SearchDocumentChunksRerankedQuery(organizationA, projectA, evaluationCase.Query, CandidateDepth),
                CancellationToken.None);

            // ToHits throws if any returned chunk id is outside Organization A's
            // seeded corpus, so a foreign-tenant leak fails the test here.
            _ = ToHits(evaluationCase.CaseId, candidate.Hits.Select(h => h.DocumentChunkId), chunkKeyById);
        }
    }

    private static List<RetrievalEvaluationHit> ToHits(
        string caseId,
        IEnumerable<Guid> orderedChunkIds,
        IReadOnlyDictionary<string, Guid> chunkKeyById)
    {
        var keyByChunkId = new Dictionary<Guid, string>();
        foreach (var (chunkKey, chunkId) in chunkKeyById)
        {
            keyByChunkId[chunkId] = chunkKey;
        }

        var hits = new List<RetrievalEvaluationHit>();
        int rank = 1;
        foreach (var chunkId in orderedChunkIds)
        {
            if (!keyByChunkId.TryGetValue(chunkId, out var chunkKey))
            {
                throw new InvalidOperationException(
                    $"Case '{caseId}' returned chunk {chunkId}, which is not part of the seeded corpus for this " +
                    "organization/project (tenant isolation breach).");
            }

            hits.Add(new RetrievalEvaluationHit(chunkKey, rank));
            rank++;
        }

        return hits;
    }

    // Proves (does not assume) that the reranker scored exactly the baseline RRF
    // pool: same count, same per-index chunk id, same original rank, same text,
    // and the exact query — with no sorting that could hide an ordering mismatch.
    private static void AssertRerankerSawBaselinePool(
        EvaluationCase evaluationCase,
        IReadOnlyList<HybridChunkHit> baselinePool,
        RecordingChunkReranker recorder)
    {
        Assert.Equal(1, recorder.RequestCount);
        Assert.NotNull(recorder.LastRequest);

        var request = recorder.LastRequest;

        Assert.Equal(evaluationCase.Query, request.Query);
        Assert.Equal(baselinePool.Count, request.Candidates.Count);

        for (int i = 0; i < baselinePool.Count; i++)
        {
            Assert.Equal(baselinePool[i].DocumentChunkId, request.Candidates[i].DocumentChunkId);
            Assert.Equal(i + 1, request.Candidates[i].OriginalHybridRank);
            Assert.Equal(baselinePool[i].Text, request.Candidates[i].Text);
        }
    }

    private static string FormatComparison(
        EvaluationDataset dataset,
        RetrievalEvaluationResult baseline,
        RetrievalEvaluationResult candidate)
    {
        var lines = new List<string>
        {
            "OpsFlow Reranked-vs-Baseline Retrieval Comparison",
            "Deterministic synthetic regression comparison (token-overlap test reranker).",
            "NOT a real-world RAG reranking quality measurement.",
            string.Empty,
            $"Dataset: {dataset.DatasetId} (v{dataset.DatasetVersion}), cases: {baseline.CaseCount.ToString(CultureInfo.InvariantCulture)}",
            $"Candidate pool depth: {CandidateDepth.ToString(CultureInfo.InvariantCulture)}",
            string.Empty,
            $"  {"K",-4}{"Recall(base)",14}{"Recall(cand)",14}{"dRecall",10}{"MRR(base)",12}{"MRR(cand)",12}{"dMRR",10}{"nDCG(base)",12}{"nDCG(cand)",12}{"dnDCG",10}",
        };

        for (int i = 0; i < baseline.Aggregates.Count; i++)
        {
            var b = baseline.Aggregates[i];
            var c = candidate.Aggregates[i];
            lines.Add(
                $"  {b.K,-4}" +
                $"{F(b.MeanRecall),14}{F(c.MeanRecall),14}{F(c.MeanRecall - b.MeanRecall),10}" +
                $"{F(b.MeanReciprocalRank),12}{F(c.MeanReciprocalRank),12}{F(c.MeanReciprocalRank - b.MeanReciprocalRank),10}" +
                $"{F(b.MeanNdcg),12}{F(c.MeanNdcg),12}{F(c.MeanNdcg - b.MeanNdcg),10}");
        }

        return string.Join("\n", lines);
    }

    private static string F(double value) => value.ToString("F4", CultureInfo.InvariantCulture);

    private async Task<IReadOnlyDictionary<string, Guid>> SeedCorpusAsync(
        string scenario,
        Guid organizationId,
        Guid projectId,
        EvaluationDataset dataset)
    {
        await using var db = _fixture.CreateContext();

        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "RerankOrg-" + organizationId.ToString("N")[..8],
            Slug = "rerank-org-" + organizationId.ToString("N")[..8],
            CreatedAt = Timestamp,
        });
        await db.SaveChangesAsync();

        db.Projects.Add(new Project(projectId, organizationId, "Rerank Evaluation Project", null, Timestamp));
        await db.SaveChangesAsync();

        var chunkKeyById = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var document in dataset.Documents)
        {
            var documentId = StableGuid($"{scenario}:document:{document.DocumentKey}");
            db.Documents.Add(new Document(
                documentId, organizationId, projectId, document.DocumentKey + ".txt", "text/plain", 100, Timestamp));
            await db.SaveChangesAsync();

            var fullText = string.Concat(document.Chunks.Select(chunk => chunk.Text));
            db.DocumentExtractions.Add(new DocumentExtraction(documentId, fullText, Timestamp));
            await db.SaveChangesAsync();

            db.DocumentChunkSets.Add(new DocumentChunkSet(documentId, 1, document.Chunks.Count, Timestamp));
            var chunkIds = new Guid[document.Chunks.Count];
            int offset = 0;
            for (int i = 0; i < document.Chunks.Count; i++)
            {
                var chunk = document.Chunks[i];
                var chunkId = StableGuid($"{scenario}:chunk:{chunk.ChunkKey}");
                chunkIds[i] = chunkId;
                db.DocumentChunks.Add(new DocumentChunk(
                    chunkId, documentId, i, offset, offset + chunk.Text.Length, chunk.Text));
                offset += chunk.Text.Length;
                chunkKeyById[chunk.ChunkKey] = chunkId;
            }

            await db.SaveChangesAsync();

            var embeddingSetId = StableGuid($"{scenario}:embedding-set:{document.DocumentKey}");
            db.DocumentEmbeddingSets.Add(new DocumentEmbeddingSet(
                embeddingSetId, documentId, 1, EmbeddingProfiles.SemanticV1Id,
                EmbeddingProfiles.SemanticV1ModelId, EmbeddingProfiles.SemanticV1Dimensions,
                document.Chunks.Count, Timestamp));

            for (int i = 0; i < document.Chunks.Count; i++)
            {
                float[] vector = DeterministicContentEmbeddingGenerator.Embed(document.Chunks[i].Text);
                db.Set<DocumentChunkEmbeddingRow>().Add(new DocumentChunkEmbeddingRow
                {
                    EmbeddingSetId = embeddingSetId,
                    DocumentChunkId = chunkIds[i],
                    Embedding = new SqlVector<float>(vector),
                });
            }

            await db.SaveChangesAsync();
        }

        return chunkKeyById;
    }

    private async Task SeedForeignDistractorAsync(
        string scenario, Guid organizationId, Guid projectId, string chunkText)
    {
        await using var db = _fixture.CreateContext();

        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "RerankForeignOrg-" + organizationId.ToString("N")[..8],
            Slug = "rerank-foreign-org-" + organizationId.ToString("N")[..8],
            CreatedAt = Timestamp,
        });
        await db.SaveChangesAsync();

        db.Projects.Add(new Project(projectId, organizationId, "Rerank Foreign Project", null, Timestamp));
        await db.SaveChangesAsync();

        var documentId = StableGuid($"{scenario}:foreign-document");
        db.Documents.Add(new Document(
            documentId, organizationId, projectId, "foreign.txt", "text/plain", 100, Timestamp));
        await db.SaveChangesAsync();

        db.DocumentExtractions.Add(new DocumentExtraction(documentId, chunkText, Timestamp));
        await db.SaveChangesAsync();

        db.DocumentChunkSets.Add(new DocumentChunkSet(documentId, 1, 1, Timestamp));
        var chunkId = StableGuid($"{scenario}:foreign-chunk");
        db.DocumentChunks.Add(new DocumentChunk(chunkId, documentId, 0, 0, chunkText.Length, chunkText));
        await db.SaveChangesAsync();

        var embeddingSetId = StableGuid($"{scenario}:foreign-embedding-set");
        db.DocumentEmbeddingSets.Add(new DocumentEmbeddingSet(
            embeddingSetId, documentId, 1, EmbeddingProfiles.SemanticV1Id,
            EmbeddingProfiles.SemanticV1ModelId, EmbeddingProfiles.SemanticV1Dimensions, 1, Timestamp));
        db.Set<DocumentChunkEmbeddingRow>().Add(new DocumentChunkEmbeddingRow
        {
            EmbeddingSetId = embeddingSetId,
            DocumentChunkId = chunkId,
            Embedding = new SqlVector<float>(DeterministicContentEmbeddingGenerator.Embed(chunkText)),
        });
        await db.SaveChangesAsync();
    }

    private async Task WaitForFullTextPopulationAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var deadline = DateTime.UtcNow + effectiveTimeout;
        await using var db = _fixture.CreateContext();

        while (DateTime.UtcNow < deadline)
        {
            var status = await db.Database
                .SqlQuery<int>($"SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID('DocumentChunks'), 'TableFulltextPopulateStatus') AS int) AS [Value]")
                .SingleAsync();
            var pending = await db.Database
                .SqlQuery<int>($"SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID('DocumentChunks'), 'TableFulltextPendingChanges') AS int) AS [Value]")
                .SingleAsync();

            if (status == 0 && pending == 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Full-text population on DocumentChunks did not complete within {effectiveTimeout.TotalSeconds}s.");
    }

    private static Guid StableGuid(string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("opsflow-retrieval-eval-id-v1:" + key));
        byte[] guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, guidBytes.Length);
        return new Guid(guidBytes);
    }

    // Test-only decorator around the deterministic scorer. It changes no scoring
    // and reads no evaluation data — it only records the exact request the
    // reranked service supplied, as an immutable snapshot, so the test can prove
    // the candidate reranked the same pool the baseline measured.
    private sealed class RecordingChunkReranker : IChunkReranker
    {
        private readonly IChunkReranker _inner;

        public RecordingChunkReranker(IChunkReranker inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        public RerankerIdentity Identity => _inner.Identity;

        public int RequestCount { get; private set; }

        public ChunkRerankRequest? LastRequest { get; private set; }

        public Task<IReadOnlyList<ChunkRerankScore>> RerankAsync(
            ChunkRerankRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            RequestCount++;

            // Immutable snapshot: copy the candidate list so nothing can mutate it
            // between recording and assertion. Candidate records are themselves
            // immutable; the query string is immutable.
            LastRequest = new ChunkRerankRequest(request.Query, [.. request.Candidates]);

            // Forward the original request and cancellation token unchanged; return
            // the inner scores verbatim.
            return _inner.RerankAsync(request, cancellationToken);
        }
    }
}
