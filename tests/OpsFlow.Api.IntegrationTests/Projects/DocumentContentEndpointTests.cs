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
        string contentType = "application/pdf")
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
    public async Task Valid_pdf_download_returns_200_with_exact_bytes()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var data = "%PDF"u8.ToArray();
        var (docId, _) = await UploadDocumentAsync(token, projectId, "report.pdf", data);

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(data, body);
    }

    [Fact]
    public async Task Valid_txt_download_returns_200_with_exact_bytes()
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
        var data = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
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
        var (docId, _) = await UploadDocumentAsync(token, projectId, "report.pdf", [0x01]);

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    // ================================================================
    // Content-Disposition
    // ================================================================

    [Fact]
    public async Task Content_disposition_is_attachment_with_filename()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var (docId, _) = await UploadDocumentAsync(token, projectId, "report.pdf", [0x01]);

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal("report.pdf", disposition.FileName?.Trim('"'));
    }

    [Fact]
    public async Task Filename_with_spaces_handled_correctly()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var (docId, _) = await UploadDocumentAsync(token, projectId, "my report.pdf", [0x01]);

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Contains("my report.pdf", disposition.ToString());
    }

    [Fact]
    public async Task Unicode_filename_handled_by_framework()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var (docId, _) = await UploadDocumentAsync(token, projectId, "été.pdf", [0x01]);

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
        var (docId, _) = await UploadDocumentAsync(token, projectId, "file (1).pdf", [0x01]);

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Contains("file (1).pdf", disposition.ToString());
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
        var (docId, _) = await UploadDocumentAsync(token, projectA, "report.pdf", [0x01]);

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
        var (docIdB, _) = await UploadDocumentAsync(tokenB, projectB, "secret.pdf", [0x01]);

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
        var (docIdB, _) = await UploadDocumentAsync(tokenB, projectB, "confidential.pdf", [0x01]);

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
        var (docIdB, _) = await UploadDocumentAsync(tokenB, projectB, "private.pdf", [0x01]);

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
        var (docId, _) = await UploadDocumentAsync(token, projectId, "ephemeral.pdf", [0x01]);

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
        var (docId, _) = await UploadDocumentAsync(token, projectId, "vanished.pdf", [0x01]);

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
        var data = new byte[8192];
        Random.Shared.NextBytes(data);
        var (docId, _) = await UploadDocumentAsync(token, projectId, "random.pdf", data);

        using var response = await _client.SendAsync(BuildContentRequest(token, projectId, docId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(data, body);
    }
}
