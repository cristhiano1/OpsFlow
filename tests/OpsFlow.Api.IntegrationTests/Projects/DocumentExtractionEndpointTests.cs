using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Api.IntegrationTests.Authentication;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Contracts.Authentication;
using OpsFlow.Contracts.Documents;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Projects;

[Collection(SqlServerCollection.Name)]
public sealed class DocumentExtractionEndpointTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string LoginPath = "/api/v1/auth/login";

    private readonly string _storageRoot;
    private readonly ExtractionTestFactory _factory;
    private readonly HttpClient _client;

    public DocumentExtractionEndpointTests(SqlServerFixture fixture)
    {
        _storageRoot = Path.Combine(Path.GetTempPath(), "opsflow-extract-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storageRoot);
        _factory = new ExtractionTestFactory(fixture.ConnectionString, _storageRoot);
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
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    // ----------------------------------------------------------------
    // Factory with storage override
    // ----------------------------------------------------------------

    private sealed class ExtractionTestFactory : WebApplicationFactory<Program>
    {
        private const string ConnectionStringEnvironmentVariable = "ConnectionStrings__OpsFlow";

        private readonly string _connectionString;
        private readonly string _storagePath;
        private readonly string? _previousConnectionString;

        public ExtractionTestFactory(string connectionString, string storagePath)
        {
            _connectionString = connectionString;
            _storagePath = storagePath;
            _previousConnectionString = Environment.GetEnvironmentVariable(
                ConnectionStringEnvironmentVariable,
                EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(
                ConnectionStringEnvironmentVariable,
                _connectionString,
                EnvironmentVariableTarget.Process);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:SigningKey"] = Convert.ToBase64String(
                        System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                    ["DocumentStorage:BasePath"] = _storagePath,
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            RestoreConnectionString();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            RestoreConnectionString();
            await base.DisposeAsync();
        }

        private void RestoreConnectionString()
        {
            Environment.SetEnvironmentVariable(
                ConnectionStringEnvironmentVariable,
                _previousConnectionString,
                EnvironmentVariableTarget.Process);
        }
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private static string ExtractionPath(Guid projectId, Guid documentId) =>
        $"/api/v1/projects/{projectId}/documents/{documentId}/extraction";

    private static string DocumentsPath(Guid projectId) =>
        $"/api/v1/projects/{projectId}/documents";

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
            Guid.NewGuid(), orgId, "Test Project", null,
            new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero));
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

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

    private async Task<Guid> UploadDocumentAsync(
        string token, Guid projectId, string fileName, byte[] data,
        string contentType = "text/plain")
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);

        var msg = new HttpRequestMessage(HttpMethod.Post, DocumentsPath(projectId))
        {
            Content = content,
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var doc = await response.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(doc);
        return doc.Id;
    }

    private HttpRequestMessage BuildPostExtractionRequest(string token, Guid projectId, Guid documentId)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, ExtractionPath(projectId, documentId));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return msg;
    }

    private HttpRequestMessage BuildGetExtractionRequest(string token, Guid projectId, Guid documentId)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, ExtractionPath(projectId, documentId));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return msg;
    }

    // ================================================================
    // Authentication
    // ================================================================

    [Fact]
    public async Task POST_extraction_without_bearer_returns_401()
    {
        using var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, ExtractionPath(Guid.NewGuid(), Guid.NewGuid())));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_extraction_without_bearer_returns_401()
    {
        using var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, ExtractionPath(Guid.NewGuid(), Guid.NewGuid())));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ================================================================
    // Not found
    // ================================================================

    [Fact]
    public async Task POST_nonexistent_document_returns_404()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildPostExtractionRequest(token, projectId, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GET_extraction_before_POST_returns_404()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var docId = await UploadDocumentAsync(
            token, projectId, "notes.txt", Encoding.UTF8.GetBytes("hello"));

        using var response = await _client.SendAsync(
            BuildGetExtractionRequest(token, projectId, docId));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ================================================================
    // PDF extraction disabled — returns 415
    // ================================================================

    [Fact]
    public async Task POST_pdf_extraction_returns_415()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var docId = await UploadDocumentAsync(
            token, projectId, "report.pdf", "%PDF"u8.ToArray(), "application/pdf");

        using var response = await _client.SendAsync(
            BuildPostExtractionRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    // ================================================================
    // TXT extraction
    // ================================================================

    [Fact]
    public async Task POST_txt_extraction_returns_201_with_extracted_text()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var docId = await UploadDocumentAsync(
            token, projectId, "notes.txt", Encoding.UTF8.GetBytes("Hello, world!"));

        using var response = await _client.SendAsync(
            BuildPostExtractionRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DocumentExtractionResponse>();
        Assert.NotNull(body);
        Assert.Equal(docId, body.DocumentId);
        Assert.Equal("Hello, world!", body.Text);
        Assert.Equal(13, body.CharacterCount);
    }

    // ================================================================
    // Caching — POST returns 200 on second call
    // ================================================================

    [Fact]
    public async Task POST_second_call_returns_200_with_same_text()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var docId = await UploadDocumentAsync(
            token, projectId, "notes.txt", Encoding.UTF8.GetBytes("cached"));

        using var first = await _client.SendAsync(
            BuildPostExtractionRequest(token, projectId, docId));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await _client.SendAsync(
            BuildPostExtractionRequest(token, projectId, docId));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<DocumentExtractionResponse>();
        Assert.NotNull(body);
        Assert.Equal("cached", body.Text);
    }

    // ================================================================
    // GET reads persisted extraction
    // ================================================================

    [Fact]
    public async Task GET_after_POST_returns_200_with_same_extraction()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var docId = await UploadDocumentAsync(
            token, projectId, "notes.txt", Encoding.UTF8.GetBytes("readable"));

        using var postResponse = await _client.SendAsync(
            BuildPostExtractionRequest(token, projectId, docId));
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);
        var postBody = await postResponse.Content.ReadFromJsonAsync<DocumentExtractionResponse>();

        using var getResponse = await _client.SendAsync(
            BuildGetExtractionRequest(token, projectId, docId));
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = await getResponse.Content.ReadFromJsonAsync<DocumentExtractionResponse>();

        Assert.NotNull(postBody);
        Assert.NotNull(getBody);
        Assert.Equal(postBody.DocumentId, getBody.DocumentId);
        Assert.Equal(postBody.Text, getBody.Text);
        Assert.Equal(postBody.CharacterCount, getBody.CharacterCount);
    }

    // ================================================================
    // Empty text extraction is valid
    // ================================================================

    [Fact]
    public async Task POST_whitespace_only_txt_returns_201_with_empty_text()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var docId = await UploadDocumentAsync(
            token, projectId, "spaces.txt", Encoding.UTF8.GetBytes("   \n  \r\n  "));

        using var response = await _client.SendAsync(
            BuildPostExtractionRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DocumentExtractionResponse>();
        Assert.NotNull(body);
        Assert.Equal(string.Empty, body.Text);
        Assert.Equal(0, body.CharacterCount);
    }

    // ================================================================
    // Text normalization end-to-end
    // ================================================================

    [Fact]
    public async Task POST_normalizes_CRLF_to_LF()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var docId = await UploadDocumentAsync(
            token, projectId, "crlf.txt", Encoding.UTF8.GetBytes("line1\r\nline2"));

        using var response = await _client.SendAsync(
            BuildPostExtractionRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DocumentExtractionResponse>();
        Assert.NotNull(body);
        Assert.Equal("line1\nline2", body.Text);
    }

    // ================================================================
    // Malformed document
    // ================================================================

    [Fact]
    public async Task POST_malformed_txt_returns_422()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var docId = await UploadDocumentAsync(
            token, projectId, "bad.txt", [0xFF, 0xFE, 0x80, 0x81]);

        using var response = await _client.SendAsync(
            BuildPostExtractionRequest(token, projectId, docId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ================================================================
    // Cross-tenant isolation
    // ================================================================

    [Fact]
    public async Task POST_cross_tenant_returns_404()
    {
        using var scope = _factory.Services.CreateScope();
        var orgA = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var orgB = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);

        var userA = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, orgA.Id, DefaultPassword, role: "Coordinator");
        var userB = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, orgB.Id, DefaultPassword, role: "Coordinator");

        var tokenA = await LoginAsync(userA.Email!);
        var tokenB = await LoginAsync(userB.Email!);

        var projectB = await SeedProjectAsync(scope.ServiceProvider, orgB.Id);
        var docIdB = await UploadDocumentAsync(
            tokenB, projectB, "secret.txt", Encoding.UTF8.GetBytes("confidential"));

        using var response = await _client.SendAsync(
            BuildPostExtractionRequest(tokenA, projectB, docIdB));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GET_cross_tenant_returns_404()
    {
        using var scope = _factory.Services.CreateScope();
        var orgA = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var orgB = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);

        var userA = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, orgA.Id, DefaultPassword, role: "Coordinator");
        var userB = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, orgB.Id, DefaultPassword, role: "Coordinator");

        var tokenA = await LoginAsync(userA.Email!);
        var tokenB = await LoginAsync(userB.Email!);

        var projectB = await SeedProjectAsync(scope.ServiceProvider, orgB.Id);
        var docIdB = await UploadDocumentAsync(
            tokenB, projectB, "secret.txt", Encoding.UTF8.GetBytes("confidential"));

        using var postResponse = await _client.SendAsync(
            BuildPostExtractionRequest(tokenB, projectB, docIdB));
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);

        using var getResponse = await _client.SendAsync(
            BuildGetExtractionRequest(tokenA, projectB, docIdB));
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    // ================================================================
    // Storage missing — POST returns 500
    // ================================================================

    [Fact]
    public async Task POST_storage_missing_returns_500()
    {
        var (token, orgId, projectId) = await SeedProjectAndLoginAsync();
        var docId = await UploadDocumentAsync(
            token, projectId, "vanished.txt", Encoding.UTF8.GetBytes("text"));

        var physicalPath = Path.Combine(
            _storageRoot,
            orgId.ToString("N"),
            projectId.ToString("N"),
            docId.ToString("N"));
        Assert.True(File.Exists(physicalPath));
        File.Delete(physicalPath);

        using var response = await _client.SendAsync(
            BuildPostExtractionRequest(token, projectId, docId));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ================================================================
    // GET never triggers extraction
    // ================================================================

    [Fact]
    public async Task GET_nonexistent_document_returns_404_not_extraction()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildGetExtractionRequest(token, projectId, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ================================================================
    // Location header on 201
    // ================================================================

    [Fact]
    public async Task POST_201_includes_location_header_pointing_to_GET()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var docId = await UploadDocumentAsync(
            token, projectId, "loc.txt", Encoding.UTF8.GetBytes("location test"));

        using var postResponse = await _client.SendAsync(
            BuildPostExtractionRequest(token, projectId, docId));
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);
        Assert.NotNull(postResponse.Headers.Location);

        var getMsg = new HttpRequestMessage(HttpMethod.Get, postResponse.Headers.Location);
        getMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResponse = await _client.SendAsync(getMsg);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = await getResponse.Content.ReadFromJsonAsync<DocumentExtractionResponse>();
        Assert.NotNull(getBody);
        Assert.Equal(docId, getBody.DocumentId);
        Assert.Equal("location test", getBody.Text);
    }

    // ================================================================
    // Response contract shape
    // ================================================================

    [Fact]
    public async Task POST_response_includes_extracted_at_timestamp()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var docId = await UploadDocumentAsync(
            token, projectId, "ts.txt", Encoding.UTF8.GetBytes("timestamped"));

        using var response = await _client.SendAsync(
            BuildPostExtractionRequest(token, projectId, docId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<DocumentExtractionResponse>();
        Assert.NotNull(body);
        Assert.True(body.ExtractedAt > DateTimeOffset.MinValue);
    }
}
