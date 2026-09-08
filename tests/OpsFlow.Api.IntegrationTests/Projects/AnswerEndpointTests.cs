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
public sealed class AnswerEndpointTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string LoginPath = "/api/v1/auth/login";
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 6, 10, 0, 0, TimeSpan.Zero);

    private readonly TestEmbeddingGenerator _fakeEmbedding = new();
    private readonly TestGroundedAnswerGenerator _fakeAnswer = new();
    private readonly OpsFlowWebApplicationFactory _baseFactory;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public AnswerEndpointTests(SqlServerFixture fixture)
    {
        _baseFactory = new OpsFlowWebApplicationFactory(fixture.ConnectionString);
        _factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmbeddingGenerator>();
                services.AddSingleton<IEmbeddingGenerator>(_fakeEmbedding);
                services.RemoveAll<IGroundedAnswerGenerator>();
                services.AddSingleton<IGroundedAnswerGenerator>(_fakeAnswer);
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

    private static string AnswerPath(Guid projectId) =>
        $"/api/v1/projects/{projectId}/answer";

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

    private static HttpRequestMessage BuildAnswerRequest(string token, Guid projectId, object? body)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, AnswerPath(projectId))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return msg;
    }

    private static HttpRequestMessage BuildAnswerRequestRaw(string token, Guid projectId, string rawJson)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, AnswerPath(projectId))
        {
            Content = new StringContent(rawJson, Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return msg;
    }

    private async Task<List<Guid>> SeedDocumentWithChunksAsync(
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

        return chunkIds;
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
    // Authentication / tenancy
    // ================================================================

    [Fact]
    public async Task Answer_without_bearer_returns_401()
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, AnswerPath(Guid.NewGuid()))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { question = "What is the process?" }),
                Encoding.UTF8, "application/json"),
        };
        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Answer_nonexistent_project_returns_404()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);

        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, Guid.NewGuid(), new { question = "What is the process?" }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _fakeAnswer.CallCount);
    }

    [Fact]
    public async Task Answer_cross_tenant_returns_404()
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
            BuildAnswerRequest(tokenA, projectB, new { question = "What is the process?" }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _fakeAnswer.CallCount);
    }

    [Fact]
    public async Task Answer_client_supplied_organizationId_in_body_is_ignored()
    {
        // The caller's own project exists in their tenant. A stray body
        // "organizationId" pointing elsewhere must not change the tenant used:
        // the request still resolves the project in the claim's organization
        // (here: an empty project -> insufficient_evidence, not 404).
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new
            {
                question = "What is the process?",
                organizationId = Guid.NewGuid(),
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AnswerProjectQuestionResponse>();
        Assert.NotNull(result);
        Assert.Equal("insufficient_evidence", result.Status);
    }

    // ================================================================
    // Validation
    // ================================================================

    [Fact]
    public async Task Answer_with_null_body_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildAnswerRequestRaw(token, projectId, "null"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Answer_with_malformed_json_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildAnswerRequestRaw(token, projectId, "{not valid json}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Answer_with_null_question_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new { }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Answer_with_whitespace_question_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new { question = "   \t\n" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Answer_with_question_exceeding_max_length_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new { question = new string('a', 2501) }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Answer_with_punctuation_only_question_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new { question = "!@#$%^&*()" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _fakeAnswer.CallCount);
    }

    [Fact]
    public async Task Answer_forwards_command_form_question_unchanged()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        const string question = "Summarize the deployment policy.";
        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new { question }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The exact original question reached retrieval (embedding) unchanged.
        Assert.NotNull(_fakeEmbedding.LastTexts);
        Assert.Single(_fakeEmbedding.LastTexts);
        Assert.Equal(question, _fakeEmbedding.LastTexts[0]);
    }

    [Fact]
    public async Task Answer_forwards_explicit_language_question_unchanged()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        const string question = "Summarize the deployment policy in Spanish.";
        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new { question }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(_fakeEmbedding.LastTexts);
        Assert.Equal(question, _fakeEmbedding.LastTexts[0]);
    }

    // ================================================================
    // Success
    // ================================================================

    [Fact]
    public async Task Answer_returns_answered_with_verified_citation()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);
        var projectId = await SeedProjectAsync(scope.ServiceProvider, org.Id);

        const string chunkText = "Machine learning algorithms optimize neural network training processes";
        var chunkIds = await SeedDocumentWithChunksAsync(
            scope.ServiceProvider, org.Id, projectId, [chunkText]);
        await WaitForFullTextPopulationAsync();

        _fakeAnswer.Output = new GroundedAnswerGenerationOutput(
            GeneratedAnswerStatus.Answered, "The deployment requires approval.", [1]);

        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new { question = "machine learning algorithms" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AnswerProjectQuestionResponse>();
        Assert.NotNull(result);
        Assert.Equal("answered", result.Status);
        Assert.Equal("The deployment requires approval.", result.Answer);

        var citation = Assert.Single(result.Citations);
        Assert.Equal(chunkIds[0], citation.DocumentChunkId);
        Assert.Equal(0, citation.ChunkIndex);
        Assert.Equal(0, citation.StartOffset);
        Assert.Equal(chunkText.Length, citation.EndOffset);
        Assert.Equal(chunkText, citation.Text);
        Assert.Equal(1, _fakeAnswer.CallCount);
    }

    [Fact]
    public async Task Answer_preserves_citation_order_from_generator()
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
        var chunkIds = await SeedDocumentWithChunksAsync(
            scope.ServiceProvider, org.Id, projectId, chunkTexts,
            vectorFactory: i => i == 0 ? MakeAlignedVector() : MakeOrthogonalVector());
        await WaitForFullTextPopulationAsync();

        // Evidence 1 -> chunk0 (ranked first), evidence 2 -> chunk1. Generator
        // cites [2, 1]; the response must preserve that order.
        _fakeAnswer.Output = new GroundedAnswerGenerationOutput(
            GeneratedAnswerStatus.Answered, "Answer.", [2, 1]);

        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new { question = "machine learning algorithms" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AnswerProjectQuestionResponse>();
        Assert.NotNull(result);
        Assert.Equal(2, result.Citations.Count);
        Assert.Equal(chunkIds[1], result.Citations[0].DocumentChunkId);
        Assert.Equal(chunkIds[0], result.Citations[1].DocumentChunkId);
    }

    [Fact]
    public async Task Answer_response_does_not_expose_internal_fields()
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

        _fakeAnswer.Output = new GroundedAnswerGenerationOutput(
            GeneratedAnswerStatus.Answered, "Grounded answer.", [1]);

        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new { question = "machine learning" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("citationNumber", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rrfScore", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("semanticRank", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lexicalRank", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cosineDistance", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ftsRank", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemPrompt", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userPrompt", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organizationId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(org.Id.ToString(), raw, StringComparison.Ordinal);
    }

    // ================================================================
    // Insufficient evidence
    // ================================================================

    [Fact]
    public async Task Answer_empty_project_returns_insufficient_without_generation()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new { question = "what is the deployment process" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AnswerProjectQuestionResponse>();
        Assert.NotNull(result);
        Assert.Equal("insufficient_evidence", result.Status);
        Assert.Null(result.Answer);
        Assert.Empty(result.Citations);
        Assert.Equal(0, _fakeAnswer.CallCount);
    }

    [Fact]
    public async Task Answer_model_declared_insufficient_returns_insufficient()
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

        _fakeAnswer.Output = new GroundedAnswerGenerationOutput(
            GeneratedAnswerStatus.InsufficientEvidence, null, []);

        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new { question = "machine learning" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AnswerProjectQuestionResponse>();
        Assert.NotNull(result);
        Assert.Equal("insufficient_evidence", result.Status);
        Assert.Null(result.Answer);
        Assert.Empty(result.Citations);
        Assert.Equal(1, _fakeAnswer.CallCount);
    }

    // ================================================================
    // Failure mapping
    // ================================================================

    [Fact]
    public async Task Answer_provider_failure_returns_503_without_details()
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

        _fakeAnswer.ExceptionToThrow = new AnswerGenerationException("provider failure sentinel");

        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new { question = "machine learning" }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("provider failure sentinel", raw);
        Assert.DoesNotContain("openai", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Answer_grounding_contract_violation_returns_502_without_details()
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

        // Exactly one evidence item is selected, so citation 999 is out of range;
        // the REAL AnswerProjectQuestionService throws GroundedAnswerValidationException.
        _fakeAnswer.Output = new GroundedAnswerGenerationOutput(
            GeneratedAnswerStatus.Answered, "Grounded-looking answer.", [999]);

        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new { question = "machine learning" }));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Grounded-looking answer", raw);
        Assert.DoesNotContain("999", raw);
    }

    [Fact]
    public async Task Answer_unexpected_failure_returns_500_without_details()
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

        _fakeAnswer.ExceptionToThrow = new InvalidOperationException("unexpected sentinel");

        using var response = await _client.SendAsync(
            BuildAnswerRequest(token, projectId, new { question = "machine learning" }));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("unexpected sentinel", raw);
    }

    // ================================================================
    // Test fakes
    // ================================================================

    private sealed class TestEmbeddingGenerator : IEmbeddingGenerator
    {
        public EmbeddingGeneratorIdentity Identity { get; } = new(
            EmbeddingProfiles.SemanticV1Id,
            EmbeddingProfiles.SemanticV1ModelId,
            EmbeddingProfiles.SemanticV1Dimensions);

        public IReadOnlyList<string>? LastTexts { get; private set; }

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken)
        {
            LastTexts = texts;

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

    private sealed class TestGroundedAnswerGenerator : IGroundedAnswerGenerator
    {
        public GroundedAnswerGenerationOutput Output { get; set; } =
            new(GeneratedAnswerStatus.Answered, "Default grounded answer.", [1]);

        public Exception? ExceptionToThrow { get; set; }

        public int CallCount { get; private set; }

        public GroundedAnswerGenerationRequest? LastRequest { get; private set; }

        public Task<GroundedAnswerGenerationOutput> GenerateAsync(
            GroundedAnswerGenerationRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(Output);
        }
    }
}
