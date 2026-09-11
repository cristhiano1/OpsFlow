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
/// SQL-backed deterministic retrieval baseline. Seeds the synthetic dataset into
/// a real SQL Server, then drives the real
/// <see cref="SearchDocumentChunksHybridService"/> — real semantic vector
/// retrieval, real full-text lexical retrieval, and real RRF — replacing only
/// the external embedding provider with a deterministic content embedding. It
/// asserts architectural invariants (tenant isolation, no duplicate results,
/// result bounds, well-formed aggregate metrics) and reports the metrics; it
/// deliberately asserts no metric-quality thresholds — the first green CI run
/// establishes the observable baseline (see ADR-008).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RetrievalBaselineEvaluationTests : IDisposable
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
    private const int EvidenceTopK = 8;

    private readonly SqlServerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly DeterministicContentEmbeddingGenerator _embedding = new();
    private readonly OpsFlowWebApplicationFactory _baseFactory;
    private readonly WebApplicationFactory<Program> _factory;

    public RetrievalBaselineEvaluationTests(SqlServerFixture fixture, ITestOutputHelper output)
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
    public async Task Baseline_evaluation_reports_metrics_and_holds_structural_invariants()
    {
        var dataset = EvaluationDatasetLoader.LoadSyntheticV1();
        var organizationId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var chunkKeyById = await SeedCorpusAsync(organizationId, projectId, dataset);
        await WaitForFullTextPopulationAsync();

        var result = await RunBaselineAsync(dataset, organizationId, projectId, chunkKeyById);

        // Every case was scored.
        Assert.Equal(dataset.Cases.Count, result.CaseCount);

        foreach (var caseResult in result.Cases)
        {
            // Semantic retrieval always returns at least one candidate for a
            // non-empty project, so the fused result is never empty.
            Assert.NotEmpty(caseResult.RetrievedChunkKeys);

            // Never more than the requested cut-off, and never a duplicate chunk.
            Assert.True(caseResult.RetrievedChunkKeys.Count <= EvidenceTopK);
            Assert.Equal(
                caseResult.RetrievedChunkKeys.Count,
                caseResult.RetrievedChunkKeys.Distinct().Count());
        }

        // Aggregate metrics are well-formed probabilities/ratios in [0, 1].
        foreach (var aggregate in result.Aggregates)
        {
            Assert.InRange(aggregate.MeanRecall, 0.0, 1.0);
            Assert.InRange(aggregate.MeanReciprocalRank, 0.0, 1.0);
            Assert.InRange(aggregate.MeanNdcg, 0.0, 1.0);
        }

        string report = RetrievalEvaluationReportFormatter.Format(result);
        Assert.Contains(
            RetrievalEvaluationReportFormatter.NotRealWorldDisclaimer, report, StringComparison.Ordinal);

        _output.WriteLine(report);
    }

    [Fact]
    public async Task Baseline_never_returns_foreign_tenant_chunks()
    {
        var dataset = EvaluationDatasetLoader.LoadSyntheticV1();

        var organizationA = Guid.NewGuid();
        var projectA = Guid.NewGuid();
        var chunkKeyById = await SeedCorpusAsync(organizationA, projectA, dataset);

        // Organization B holds a distractor chunk whose text is nearly identical
        // to a high-value Organization A chunk. If tenant scoping were broken it
        // would surface in Organization A's rankings.
        var organizationB = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        await SeedForeignDistractorAsync(
            organizationB,
            projectB,
            "If a production deployment fails its health checks, roll it back by redeploying " +
            "the previous known-good release tag and invalidating the CDN cache.");

        await WaitForFullTextPopulationAsync();

        // RunBaselineAsync maps every returned chunk id through Organization A's
        // seed map and throws if a foreign id appears, so completing without
        // throwing is the tenant-isolation guarantee.
        var result = await RunBaselineAsync(dataset, organizationA, projectA, chunkKeyById);
        Assert.Equal(dataset.Cases.Count, result.CaseCount);
    }

    private async Task<RetrievalEvaluationResult> RunBaselineAsync(
        EvaluationDataset dataset,
        Guid organizationId,
        Guid projectId,
        IReadOnlyDictionary<string, Guid> chunkKeyById)
    {
        var keyByChunkId = new Dictionary<Guid, string>();
        foreach (var (chunkKey, chunkId) in chunkKeyById)
        {
            keyByChunkId[chunkId] = chunkKey;
        }

        var rankedResultsByCaseId = new Dictionary<string, IReadOnlyList<RetrievalEvaluationHit>>(StringComparer.Ordinal);

        using var scope = _factory.Services.CreateScope();
        var hybrid = scope.ServiceProvider.GetRequiredService<SearchDocumentChunksHybridService>();

        foreach (var evaluationCase in dataset.Cases)
        {
            var searchResult = await hybrid.SearchAsync(
                new SearchDocumentChunksHybridQuery(
                    organizationId, projectId, evaluationCase.Query, EvidenceTopK),
                CancellationToken.None);

            Assert.True(searchResult.ProjectFound);

            var hits = new List<RetrievalEvaluationHit>(searchResult.Hits.Count);
            for (int i = 0; i < searchResult.Hits.Count; i++)
            {
                Guid chunkId = searchResult.Hits[i].DocumentChunkId;
                if (!keyByChunkId.TryGetValue(chunkId, out var chunkKey))
                {
                    throw new InvalidOperationException(
                        $"Case '{evaluationCase.CaseId}' returned chunk {chunkId}, which is not part of the " +
                        "seeded corpus for this organization/project (tenant isolation breach).");
                }

                hits.Add(new RetrievalEvaluationHit(chunkKey, i + 1));
            }

            rankedResultsByCaseId[evaluationCase.CaseId] = hits;
        }

        return RetrievalEvaluator.Evaluate(dataset, rankedResultsByCaseId);
    }

    private async Task<IReadOnlyDictionary<string, Guid>> SeedCorpusAsync(
        Guid organizationId,
        Guid projectId,
        EvaluationDataset dataset)
    {
        await using var db = _fixture.CreateContext();

        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "EvalOrg-" + organizationId.ToString("N")[..8],
            Slug = "eval-org-" + organizationId.ToString("N")[..8],
            CreatedAt = Timestamp,
        });
        await db.SaveChangesAsync();

        db.Projects.Add(new Project(projectId, organizationId, "Evaluation Project", null, Timestamp));
        await db.SaveChangesAsync();

        var chunkKeyById = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var document in dataset.Documents)
        {
            var documentId = Guid.NewGuid();
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
                var chunkId = Guid.NewGuid();
                chunkIds[i] = chunkId;
                db.DocumentChunks.Add(new DocumentChunk(
                    chunkId, documentId, i, offset, offset + chunk.Text.Length, chunk.Text));
                offset += chunk.Text.Length;
                chunkKeyById[chunk.ChunkKey] = chunkId;
            }

            await db.SaveChangesAsync();

            var embeddingSetId = Guid.NewGuid();
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

    private async Task SeedForeignDistractorAsync(Guid organizationId, Guid projectId, string chunkText)
    {
        await using var db = _fixture.CreateContext();

        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "ForeignOrg-" + organizationId.ToString("N")[..8],
            Slug = "foreign-org-" + organizationId.ToString("N")[..8],
            CreatedAt = Timestamp,
        });
        await db.SaveChangesAsync();

        db.Projects.Add(new Project(projectId, organizationId, "Foreign Project", null, Timestamp));
        await db.SaveChangesAsync();

        var documentId = Guid.NewGuid();
        db.Documents.Add(new Document(
            documentId, organizationId, projectId, "foreign.txt", "text/plain", 100, Timestamp));
        await db.SaveChangesAsync();

        db.DocumentExtractions.Add(new DocumentExtraction(documentId, chunkText, Timestamp));
        await db.SaveChangesAsync();

        db.DocumentChunkSets.Add(new DocumentChunkSet(documentId, 1, 1, Timestamp));
        var chunkId = Guid.NewGuid();
        db.DocumentChunks.Add(new DocumentChunk(chunkId, documentId, 0, 0, chunkText.Length, chunkText));
        await db.SaveChangesAsync();

        var embeddingSetId = Guid.NewGuid();
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
}
