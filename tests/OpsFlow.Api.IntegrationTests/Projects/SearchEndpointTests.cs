using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpsFlow.Api.IntegrationTests.Authentication;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Application.Documents;
using OpsFlow.Contracts.Authentication;
using OpsFlow.Contracts.Documents;
using OpsFlow.Domain.Documents;
using OpsFlow.Infrastructure.Documents;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Projects;

[Collection(SqlServerCollection.Name)]
public sealed class SearchEndpointTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string LoginPath = "/api/v1/auth/login";
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private readonly TestEmbeddingGenerator _fakeGenerator = new();
    private readonly OpsFlowWebApplicationFactory _baseFactory;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SearchEndpointTests(SqlServerFixture fixture)
    {
        _baseFactory = new OpsFlowWebApplicationFactory(fixture.ConnectionString);
        _factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmbeddingGenerator>();
                services.AddSingleton<IEmbeddingGenerator>(_fakeGenerator);
            });
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private static string SearchPath(Guid projectId) =>
        $"/api/v1/projects/{projectId}/search";

    private async Task<string> LoginAsync(string email)
    {
        var json = JsonSerializer.Serialize(new { email, password = DefaultPassword });
        using var msg = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);
        return body.AccessToken;
    }

    private async Task<(string Token, Guid OrgId, Guid ProjectId)> SeedProjectAndLoginAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);
        var projectId = await SeedProjectAsync(scope.ServiceProvider, org.Id);
        return (token, org.Id, projectId);
    }

    private static async Task<Guid> SeedProjectAsync(IServiceProvider scopeServices, Guid orgId)
    {
        var db = scopeServices.GetRequiredService<OpsFlowDbContext>();
        var project = new OpsFlow.Domain.Projects.Project(
            Guid.NewGuid(), orgId, "Test Project", null, Timestamp);
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static HttpRequestMessage BuildSearchRequest(
        string token, Guid projectId, object? body)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, SearchPath(projectId))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return msg;
    }

    private static HttpRequestMessage BuildSearchRequestRaw(
        string token, Guid projectId, string rawJson)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, SearchPath(projectId))
        {
            Content = new StringContent(rawJson, Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return msg;
    }

    private async Task<(Guid DocumentId, List<Guid> ChunkIds)> SeedDocumentWithChunksAsync(
        IServiceProvider scopeServices,
        Guid orgId,
        Guid projectId,
        string[] chunkTexts,
        Func<int, float[]>? vectorFactory = null)
    {
        var db = scopeServices.GetRequiredService<OpsFlowDbContext>();

        var docId = Guid.NewGuid();
        db.Documents.Add(new Document(
            docId, orgId, projectId, "test.txt", "text/plain", 100, Timestamp));
        await db.SaveChangesAsync();

        var fullText = string.Concat(chunkTexts);
        db.DocumentExtractions.Add(new DocumentExtraction(
            docId, fullText.Length > 0 ? fullText : "empty", Timestamp));
        await db.SaveChangesAsync();

        db.DocumentChunkSets.Add(new DocumentChunkSet(docId, 1, chunkTexts.Length, Timestamp));
        var chunkIds = new List<Guid>();
        var offset = 0;
        for (int i = 0; i < chunkTexts.Length; i++)
        {
            var text = chunkTexts[i];
            var chunkId = Guid.NewGuid();
            chunkIds.Add(chunkId);
            db.DocumentChunks.Add(new DocumentChunk(
                chunkId, docId, i, offset, offset + text.Length, text));
            offset += text.Length;
        }
        await db.SaveChangesAsync();

        var setId = Guid.NewGuid();
        db.DocumentEmbeddingSets.Add(new DocumentEmbeddingSet(
            setId, docId, 1, EmbeddingProfiles.SemanticV1Id,
            EmbeddingProfiles.SemanticV1ModelId, EmbeddingProfiles.SemanticV1Dimensions,
            chunkTexts.Length, Timestamp));

        for (int i = 0; i < chunkTexts.Length; i++)
        {
            var vec = vectorFactory?.Invoke(i) ?? MakeAlignedVector();
            db.Set<DocumentChunkEmbeddingRow>().Add(new DocumentChunkEmbeddingRow
            {
                EmbeddingSetId = setId,
                DocumentChunkId = chunkIds[i],
                Embedding = new SqlVector<float>(vec),
            });
        }
        await db.SaveChangesAsync();

        return (docId, chunkIds);
    }

    private static float[] MakeAlignedVector()
    {
        var v = new float[EmbeddingProfiles.SemanticV1Dimensions];
        v[0] = 1.0f;
        return v;
    }

    private static float[] MakeOrthogonalVector()
    {
        var v = new float[EmbeddingProfiles.SemanticV1Dimensions];
        v[1] = 1.0f;
        return v;
    }

    private async Task WaitForFullTextPopulationAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
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
            "Full-text population on DocumentChunks did not complete within 30s.");
    }

    // ================================================================
    // Authentication
    // ================================================================

    [Fact]
    public async Task Search_without_bearer_returns_401()
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, SearchPath(Guid.NewGuid()))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { queryText = "test" }),
                Encoding.UTF8, "application/json"),
        };
        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ================================================================
    // Model binding
    // ================================================================

    [Fact]
    public async Task Search_with_null_body_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildSearchRequestRaw(token, projectId, "null"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_with_malformed_json_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildSearchRequestRaw(token, projectId, "{not valid json}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ================================================================
    // Query validation
    // ================================================================

    [Fact]
    public async Task Search_with_null_queryText_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { topK = 5 }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_with_empty_queryText_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = "" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_with_whitespace_queryText_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = "   " }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_with_punctuation_only_queryText_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = "!@#$%^&*()" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_with_queryText_exceeding_max_length_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = new string('a', 2501) }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_with_topK_zero_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = "test", topK = 0 }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_with_negative_topK_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = "test", topK = -1 }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_with_topK_exceeding_max_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = "test", topK = 51 }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_with_supplementary_unicode_letter_is_accepted()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var supplementary = char.ConvertFromUtf32(0x10400);
        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = supplementary }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ================================================================
    // Tenant isolation
    // ================================================================

    [Fact]
    public async Task Search_nonexistent_project_returns_404()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);

        using var response = await _client.SendAsync(
            BuildSearchRequest(token, Guid.NewGuid(), new { queryText = "test" }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Search_cross_tenant_returns_404()
    {
        using var scope = _factory.Services.CreateScope();
        var orgA = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var orgB = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);

        var userA = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, orgA.Id, DefaultPassword, role: "Coordinator");
        await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, orgB.Id, DefaultPassword, role: "Coordinator");

        var tokenA = await LoginAsync(userA.Email!);
        var projectB = await SeedProjectAsync(scope.ServiceProvider, orgB.Id);

        using var response = await _client.SendAsync(
            BuildSearchRequest(tokenA, projectB, new { queryText = "test" }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ================================================================
    // Empty result
    // ================================================================

    [Fact]
    public async Task Search_empty_project_returns_200_with_empty_items()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = "test query" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<SearchDocumentChunksResponse>();
        Assert.NotNull(result);
        Assert.Empty(result.Items);
    }

    // ================================================================
    // Success — hits with correct fields
    // ================================================================

    [Fact]
    public async Task Search_returns_hits_with_correct_fields()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);
        var projectId = await SeedProjectAsync(scope.ServiceProvider, org.Id);

        var chunkText = "Machine learning algorithms optimize neural network training processes";
        var (_, chunkIds) = await SeedDocumentWithChunksAsync(
            scope.ServiceProvider, org.Id, projectId, [chunkText]);
        await WaitForFullTextPopulationAsync();

        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = "machine learning algorithms" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<SearchDocumentChunksResponse>();
        Assert.NotNull(result);
        Assert.NotEmpty(result.Items);

        var hit = result.Items.First(h => h.DocumentChunkId == chunkIds[0]);
        Assert.Equal(0, hit.ChunkIndex);
        Assert.Equal(0, hit.StartOffset);
        Assert.Equal(chunkText.Length, hit.EndOffset);
        Assert.Equal(chunkText, hit.Text);
    }

    // ================================================================
    // TopK default
    // ================================================================

    [Fact]
    public async Task Search_omitting_topK_defaults_to_10()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);
        var projectId = await SeedProjectAsync(scope.ServiceProvider, org.Id);

        var chunkTexts = Enumerable.Range(1, 12)
            .Select(i => $"test data batch chunk number {i}")
            .ToArray();
        await SeedDocumentWithChunksAsync(
            scope.ServiceProvider, org.Id, projectId, chunkTexts);
        await WaitForFullTextPopulationAsync();

        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = "test data batch" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<SearchDocumentChunksResponse>();
        Assert.NotNull(result);
        Assert.Equal(10, result.Items.Count);
    }

    // ================================================================
    // TopK max boundary
    // ================================================================

    [Fact]
    public async Task Search_topK_50_is_accepted()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = "test", topK = 50 }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ================================================================
    // Response ordering
    // ================================================================

    [Fact]
    public async Task Search_response_preserves_relevance_order()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);
        var projectId = await SeedProjectAsync(scope.ServiceProvider, org.Id);

        var chunkTexts = new[]
        {
            "machine learning algorithms optimize training",
            "quantum computing research advances rapidly",
        };
        var (_, chunkIds) = await SeedDocumentWithChunksAsync(
            scope.ServiceProvider, org.Id, projectId, chunkTexts,
            vectorFactory: i => i == 0 ? MakeAlignedVector() : MakeOrthogonalVector());
        await WaitForFullTextPopulationAsync();

        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = "machine learning algorithms" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<SearchDocumentChunksResponse>();
        Assert.NotNull(result);
        Assert.True(result.Items.Count >= 1);

        Assert.Equal(chunkIds[0], result.Items[0].DocumentChunkId);
    }

    // ================================================================
    // Response data-leak checks
    // ================================================================

    [Fact]
    public async Task Search_response_does_not_contain_scoring_fields()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);
        var projectId = await SeedProjectAsync(scope.ServiceProvider, org.Id);

        await SeedDocumentWithChunksAsync(
            scope.ServiceProvider, org.Id, projectId,
            ["Machine learning algorithms optimize training"]);
        await WaitForFullTextPopulationAsync();

        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = "machine learning" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("rrfScore", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("semanticRank", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lexicalRank", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cosineDistance", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ftsRank", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_response_does_not_contain_organization_id()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);
        var projectId = await SeedProjectAsync(scope.ServiceProvider, org.Id);

        await SeedDocumentWithChunksAsync(
            scope.ServiceProvider, org.Id, projectId,
            ["Machine learning algorithms optimize training"]);
        await WaitForFullTextPopulationAsync();

        using var response = await _client.SendAsync(
            BuildSearchRequest(token, projectId, new { queryText = "machine learning" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("organizationId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("org_id", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(org.Id.ToString(), raw, StringComparison.Ordinal);
    }

    // ================================================================
    // Provider failure
    // ================================================================

    [Fact]
    public async Task Search_provider_failure_returns_503_without_details()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        _fakeGenerator.ExceptionToThrow = new EmbeddingGenerationException(
            "synthetic provider failure");
        try
        {
            using var response = await _client.SendAsync(
                BuildSearchRequest(token, projectId, new { queryText = "test query" }));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            var raw = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("synthetic provider failure", raw);
            Assert.DoesNotContain("openai", raw, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _fakeGenerator.ExceptionToThrow = null;
        }
    }

    // ================================================================
    // Internal failure
    // ================================================================

    [Fact]
    public async Task Search_internal_failure_returns_500_without_details()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        _fakeGenerator.ExceptionToThrow = new InvalidOperationException(
            "test internal failure");
        try
        {
            using var response = await _client.SendAsync(
                BuildSearchRequest(token, projectId, new { queryText = "test query" }));
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            var raw = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("test internal failure", raw);
        }
        finally
        {
            _fakeGenerator.ExceptionToThrow = null;
        }
    }

    // ================================================================
    // Test fake
    // ================================================================

    private sealed class TestEmbeddingGenerator : IEmbeddingGenerator
    {
        public EmbeddingGeneratorIdentity Identity { get; } = new(
            EmbeddingProfiles.SemanticV1Id,
            EmbeddingProfiles.SemanticV1ModelId,
            EmbeddingProfiles.SemanticV1Dimensions);

        public Exception? ExceptionToThrow { get; set; }

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            IReadOnlyList<ReadOnlyMemory<float>> result =
                [.. texts.Select(_ =>
                {
                    var v = new float[EmbeddingProfiles.SemanticV1Dimensions];
                    v[0] = 1.0f;
                    return (ReadOnlyMemory<float>)v;
                })];

            return Task.FromResult(result);
        }
    }
}
