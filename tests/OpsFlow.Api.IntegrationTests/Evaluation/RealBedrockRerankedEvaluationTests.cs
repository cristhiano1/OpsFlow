using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using OpsFlow.Api.IntegrationTests.Authentication;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;
using OpsFlow.Domain.Organizations;
using OpsFlow.Domain.Projects;
using OpsFlow.Evaluation.Dataset;
using OpsFlow.Evaluation.Retrieval;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Documents;
using Xunit.Abstractions;

namespace OpsFlow.Api.IntegrationTests.Evaluation;

/// <summary>
/// MANUAL, opt-in real-provider reranking evaluation against Amazon Bedrock
/// (Cohere Rerank 3.5). It reuses the deterministic synthetic corpus, the real
/// SQL hybrid-retrieval baseline, the fair-pool proof, and the shared
/// <see cref="RetrievalEvaluator"/> metrics, replacing only the reranker with the
/// production <see cref="BedrockChunkReranker"/>.
///
/// <para>
/// This test does NOT run in ordinary CI: it skips unless
/// <c>OPSFLOW_RUN_REAL_RERANK_EVAL=true</c> is set, and it never contacts AWS
/// when skipped. When enabled it additionally requires valid AWS credentials and
/// a SQL container. It is REPORT-ONLY — it applies no quality threshold and never
/// asserts the real reranker beats the baseline (see ADR-010).
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RealBedrockRerankedEvaluationTests : IDisposable
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
    private const int CandidateDepth = 20;

    private readonly SqlServerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly DeterministicContentEmbeddingGenerator _embedding = new();
    private readonly OpsFlowWebApplicationFactory _baseFactory;
    private readonly WebApplicationFactory<Program> _factory;

    public RealBedrockRerankedEvaluationTests(SqlServerFixture fixture, ITestOutputHelper output)
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

    // Discovery-time conditional skip: this test is reported as Skipped (with a
    // reason) unless the opt-in flag is set, so ordinary CI never runs its body
    // and never contacts AWS. It is not a silent empty pass.
    [RealRerankEvalFact]
    public async Task Real_bedrock_reranked_vs_baseline_reports_metrics()
    {
        const string scenario = "real-bedrock-rerank";
        var dataset = EvaluationDatasetLoader.LoadSyntheticV1();
        var organizationId = StableGuid($"{scenario}:org-a");
        var projectId = StableGuid($"{scenario}:project-a");

        var chunkKeyById = await SeedCorpusAsync(scenario, organizationId, projectId, dataset);
        await WaitForFullTextPopulationAsync();

        var options = Options.Create(new BedrockRerankerOptions
        {
            Region = "eu-central-1",
            ModelId = "cohere.rerank-v3-5:0",
            TimeoutSeconds = 30,
        });

        var baselineByCaseId = new Dictionary<string, IReadOnlyList<RetrievalEvaluationHit>>(StringComparer.Ordinal);
        var candidateByCaseId = new Dictionary<string, IReadOnlyList<RetrievalEvaluationHit>>(StringComparer.Ordinal);

        using (var scope = _factory.Services.CreateScope())
        using (var invoker = new BedrockRerankInvoker(options, NullLogger<BedrockRerankInvoker>.Instance))
        {
            var hybrid = scope.ServiceProvider.GetRequiredService<SearchDocumentChunksHybridService>();
            var realReranker = new BedrockChunkReranker(invoker, options);

            foreach (var evaluationCase in dataset.Cases)
            {
                var baseline = await hybrid.SearchAsync(
                    new SearchDocumentChunksHybridQuery(organizationId, projectId, evaluationCase.Query, CandidateDepth),
                    CancellationToken.None);
                Assert.True(baseline.ProjectFound);

                var recorder = new RecordingChunkReranker(realReranker);
                var reranked = new SearchDocumentChunksRerankedService(hybrid, recorder);

                var candidate = await reranked.SearchAsync(
                    new SearchDocumentChunksRerankedQuery(organizationId, projectId, evaluationCase.Query, CandidateDepth),
                    CancellationToken.None);
                Assert.True(candidate.ProjectFound);

                AssertRerankerSawBaselinePool(evaluationCase, baseline.Hits, recorder);

                baselineByCaseId[evaluationCase.CaseId] =
                    ToHits(evaluationCase.CaseId, baseline.Hits.Select(h => h.DocumentChunkId), chunkKeyById);
                candidateByCaseId[evaluationCase.CaseId] =
                    ToHits(evaluationCase.CaseId, candidate.Hits.Select(h => h.DocumentChunkId), chunkKeyById);
            }
        }

        var baselineResult = RetrievalEvaluator.Evaluate(dataset, baselineByCaseId);
        var candidateResult = RetrievalEvaluator.Evaluate(dataset, candidateByCaseId);

        foreach (var aggregate in baselineResult.Aggregates.Concat(candidateResult.Aggregates))
        {
            Assert.InRange(aggregate.MeanRecall, 0.0, 1.0);
            Assert.InRange(aggregate.MeanReciprocalRank, 0.0, 1.0);
            Assert.InRange(aggregate.MeanNdcg, 0.0, 1.0);
        }

        _output.WriteLine(FormatComparison(dataset, baselineResult, candidateResult));
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
                    $"Case '{caseId}' returned chunk {chunkId}, which is not part of the seeded corpus " +
                    "for this organization/project (tenant isolation breach).");
            }

            hits.Add(new RetrievalEvaluationHit(chunkKey, rank));
            rank++;
        }

        return hits;
    }

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
            "OpsFlow Real-Provider Reranked-vs-Baseline Retrieval Comparison",
            "Provider: Amazon Bedrock, model cohere.rerank-v3-5:0 (eu-central-1).",
            "REPORT-ONLY: no quality threshold; not a pass/fail on reranking quality.",
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
            Name = "RealRerankOrg-" + organizationId.ToString("N")[..8],
            Slug = "real-rerank-org-" + organizationId.ToString("N")[..8],
            CreatedAt = Timestamp,
        });
        await db.SaveChangesAsync();

        db.Projects.Add(new Project(projectId, organizationId, "Real Rerank Evaluation Project", null, Timestamp));
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

    // Test-only decorator: records the exact request the reranked service supplied
    // (an immutable snapshot) so the test can prove the real reranker scored the
    // same pool the baseline measured. It changes no scoring.
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
            LastRequest = new ChunkRerankRequest(request.Query, [.. request.Candidates]);
            return _inner.RerankAsync(request, cancellationToken);
        }
    }
}

/// <summary>
/// Marks a test that runs only when <c>OPSFLOW_RUN_REAL_RERANK_EVAL=true</c>. When
/// the flag is absent the test is reported as Skipped (with a reason) at
/// discovery time, so ordinary CI never executes its body and never contacts AWS.
/// </summary>
internal sealed class RealRerankEvalFactAttribute : FactAttribute
{
    public const string OptInVariable = "OPSFLOW_RUN_REAL_RERANK_EVAL";

    public RealRerankEvalFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip =
                $"Real-provider Bedrock evaluation is opt-in. Set {OptInVariable}=true (with valid AWS " +
                "credentials and a SQL container) to run it. Skipped so ordinary CI never calls AWS.";
        }
    }
}
