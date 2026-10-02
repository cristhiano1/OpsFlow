using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpsFlow.Api.IntegrationTests.Authentication;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Application.Documents;
using OpsFlow.Contracts.Authentication;
using OpsFlow.Contracts.Documents;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Projects;

[Collection(SqlServerCollection.Name)]
public sealed class DocumentContentEndpointTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string LoginPath = "/api/v1/auth/login";

    private readonly string _storageRoot;
    private readonly ContentTestFactory _factory;
    private readonly HttpClient _client;

    public DocumentContentEndpointTests(SqlServerFixture fixture)
    {
        _storageRoot = Path.Combine(Path.GetTempPath(), "opsflow-content-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storageRoot);
        _factory = new ContentTestFactory(fixture.ConnectionString, _storageRoot);
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

    private sealed class ContentTestFactory : WebApplicationFactory<Program>
    {
        private const string ConnectionStringEnvironmentVariable = "ConnectionStrings__OpsFlow";

        private readonly string _connectionString;
        private readonly string _storagePath;
        private readonly string? _previousConnectionString;

        public ContentTestFactory(string connectionString, string storagePath)
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
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmbeddingGenerator>();
                services.AddSingleton<IEmbeddingGenerator, DeterministicContentTestEmbeddingGenerator>();
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

    private sealed class DeterministicContentTestEmbeddingGenerator : IEmbeddingGenerator
    {
        public EmbeddingGeneratorIdentity Identity { get; } = new(
            EmbeddingProfiles.SemanticV1Id,
            EmbeddingProfiles.SemanticV1ModelId,
            EmbeddingProfiles.SemanticV1Dimensions);

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken)
        {
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

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private static string ContentPath(Guid projectId, Guid documentId) =>
        $"/api/v1/projects/{projectId}/documents/{documentId}/content";

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

    private async Task<(Guid DocumentId, byte[] Data)> UploadDocumentAsync(
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
        return (doc.Id, data);
    }

    private static HttpRequestMessage BuildContentRequest(string token, Guid projectId, Guid documentId)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, ContentPath(projectId, documentId));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return msg;
    }

    private static byte[] CreateMinimalDocx(string text = "test")
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(
                new Body(new Paragraph(new Run(new Text(text)))));
            mainPart.Document.Save();
        }

        return ms.ToArray();
    }

    // ================================================================
    // Unauthenticated
    // ================================================================

    [Fact]
    public async Task Content_without_bearer_returns_401()
    {
        using var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, ContentPath(Guid.NewGuid(), Guid.NewGuid())));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ================================================================
    // Successful downloads
    // ================================================================

    [Fact]
    public async Task Valid_txt_download_returns_200_with_exact_bytes()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var data = "Hello, world!"u8.ToArray();
        var (docId, _) = await UploadDocumentAsync(token, projectId, "report.txt", data);

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(data, body);
    }

    [Fact]
    public async Task Valid_txt_download_with_explicit_mime_returns_200()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var data = Encoding.UTF8.GetBytes("Hello, world!");
        var (docId, _) = await UploadDocumentAsync(token, projectId, "notes.txt", data, "text/plain");

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(data, body);
    }

    [Fact]
    public async Task Valid_docx_download_returns_200_with_exact_bytes()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var data = CreateMinimalDocx("contract content");
        var docxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        var (docId, _) = await UploadDocumentAsync(token, projectId, "contract.docx", data, docxMime);

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(data, body);
    }

    // ================================================================
    // Content-Type
    // ================================================================

    [Fact]
    public async Task Content_type_matches_canonical_mime()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var (docId, _) = await UploadDocumentAsync(token, projectId, "report.txt", [0x01]);

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    // ================================================================
    // Content-Disposition
    // ================================================================

    [Fact]
    public async Task Content_disposition_is_attachment_with_filename()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var (docId, _) = await UploadDocumentAsync(token, projectId, "report.txt", [0x01]);

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal("report.txt", disposition.FileName?.Trim('"'));
    }

    [Fact]
    public async Task Filename_with_spaces_handled_correctly()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var (docId, _) = await UploadDocumentAsync(token, projectId, "my report.txt", [0x01]);

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Contains("my report.txt", disposition.ToString());
    }

    [Fact]
    public async Task Unicode_filename_handled_by_framework()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var (docId, _) = await UploadDocumentAsync(token, projectId, "été.txt", [0x01]);

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition.DispositionType);
        var headerValue = disposition.ToString();
        Assert.Contains("filename*=", headerValue);
    }

    [Fact]
    public async Task Unusual_safe_filename_handled_by_framework()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var (docId, _) = await UploadDocumentAsync(token, projectId, "file (1).txt", [0x01]);

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Contains("file (1).txt", disposition.ToString());
    }

    // ================================================================
    // Not found
    // ================================================================

    [Fact]
    public async Task Nonexistent_document_returns_404()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildContentRequest(token, projectId, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_project_returns_404()
    {
        var (token, orgId, projectA) = await SeedProjectAndLoginAsync();
        var (docId, _) = await UploadDocumentAsync(token, projectA, "report.txt", [0x01]);

        using var scope = _factory.Services.CreateScope();
        var projectB = await SeedProjectAsync(scope.ServiceProvider, orgId);

        using var response = await _client.SendAsync(
            BuildContentRequest(token, projectB, docId));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ================================================================
    // Tenant isolation
    // ================================================================

    [Fact]
    public async Task Cross_tenant_project_returns_404()
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
        var (docIdB, _) = await UploadDocumentAsync(tokenB, projectB, "secret.txt", [0x01]);

        using var response = await _client.SendAsync(
            BuildContentRequest(tokenA, projectB, docIdB));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Query_organizationId_cannot_override_jwt_tenant()
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
        var (docIdB, _) = await UploadDocumentAsync(tokenB, projectB, "confidential.txt", [0x01]);

        var url = $"{ContentPath(projectB, docIdB)}?organizationId={orgB.Id}";
        var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);

        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Header_organizationId_cannot_override_jwt_tenant()
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
        var (docIdB, _) = await UploadDocumentAsync(tokenB, projectB, "private.txt", [0x01]);

        var msg = new HttpRequestMessage(HttpMethod.Get, ContentPath(projectB, docIdB));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        msg.Headers.Add("X-Organization-Id", orgB.Id.ToString());

        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ================================================================
    // Storage missing — 500
    // ================================================================

    [Fact]
    public async Task Metadata_exists_but_physical_file_missing_returns_500()
    {
        var (token, orgId, projectId) = await SeedProjectAndLoginAsync();
        var (docId, _) = await UploadDocumentAsync(token, projectId, "ephemeral.txt", [0x01]);

        var physicalPath = Path.Combine(
            _storageRoot,
            orgId.ToString("N"),
            projectId.ToString("N"),
            docId.ToString("N"));
        Assert.True(File.Exists(physicalPath));
        File.Delete(physicalPath);

        using var response = await _client.SendAsync(
            BuildContentRequest(token, projectId, docId));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Storage_missing_response_does_not_contain_physical_path()
    {
        var (token, orgId, projectId) = await SeedProjectAndLoginAsync();
        var (docId, _) = await UploadDocumentAsync(token, projectId, "vanished.txt", [0x01]);

        var physicalPath = Path.Combine(
            _storageRoot,
            orgId.ToString("N"),
            projectId.ToString("N"),
            docId.ToString("N"));
        File.Delete(physicalPath);

        using var response = await _client.SendAsync(
            BuildContentRequest(token, projectId, docId));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_storageRoot, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(orgId.ToString("N"), body, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    // Response body matches stored bytes exactly
    // ================================================================

    [Fact]
    public async Task Response_body_exactly_matches_stored_bytes()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var data = Encoding.UTF8.GetBytes(new string('A', 8192));
        var (docId, _) = await UploadDocumentAsync(token, projectId, "bulk.txt", data);

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(data, body);
    }
}
