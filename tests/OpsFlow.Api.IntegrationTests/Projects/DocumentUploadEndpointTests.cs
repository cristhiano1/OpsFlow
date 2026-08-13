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
public sealed class DocumentUploadEndpointTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string LoginPath = "/api/v1/auth/login";

    private readonly string _storageRoot;
    private readonly UploadTestFactory _factory;
    private readonly HttpClient _client;

    public DocumentUploadEndpointTests(SqlServerFixture fixture)
    {
        _storageRoot = Path.Combine(Path.GetTempPath(), "opsflow-upload-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storageRoot);
        _factory = new UploadTestFactory(fixture.ConnectionString, _storageRoot);
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

    private sealed class UploadTestFactory : WebApplicationFactory<Program>
    {
        private const string ConnectionStringEnvironmentVariable = "ConnectionStrings__OpsFlow";

        private readonly string _connectionString;
        private readonly string _storagePath;
        private readonly string? _previousConnectionString;

        public UploadTestFactory(string connectionString, string storagePath)
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
            new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero));
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

    private static MultipartFormDataContent CreateFileContent(
        string fileName,
        byte[] data,
        string contentType = "application/pdf")
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);
        return content;
    }

    private static HttpRequestMessage BuildUploadRequest(
        string token,
        Guid projectId,
        string fileName,
        byte[] data,
        string contentType = "application/pdf")
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, DocumentsPath(projectId))
        {
            Content = CreateFileContent(fileName, data, contentType),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return msg;
    }

    private static HttpRequestMessage BuildListRequest(string token, Guid projectId)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, DocumentsPath(projectId));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return msg;
    }

    // ================================================================
    // Unauthenticated
    // ================================================================

    [Fact]
    public async Task Upload_without_bearer_returns_401()
    {
        var content = CreateFileContent("test.pdf", [0x01]);
        using var msg = new HttpRequestMessage(HttpMethod.Post, DocumentsPath(Guid.NewGuid()))
        {
            Content = content,
        };
        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ================================================================
    // Successful uploads
    // ================================================================

    [Fact]
    public async Task Valid_pdf_upload_returns_201_with_metadata()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var data = "%PDF"u8.ToArray();

        using var response = await _client.SendAsync(
            BuildUploadRequest(token, projectId, "report.pdf", data));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var doc = await response.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(doc);
        Assert.NotEqual(Guid.Empty, doc.Id);
        Assert.Equal("report.pdf", doc.OriginalFileName);
        Assert.Equal("application/pdf", doc.ContentType);
        Assert.Equal(data.Length, doc.SizeBytes);
    }

    [Fact]
    public async Task Valid_txt_upload_returns_201()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var data = Encoding.UTF8.GetBytes("Hello, world!");

        using var response = await _client.SendAsync(
            BuildUploadRequest(token, projectId, "notes.txt", data, "text/plain"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var doc = await response.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(doc);
        Assert.Equal("notes.txt", doc.OriginalFileName);
        Assert.Equal("text/plain", doc.ContentType);
    }

    [Fact]
    public async Task Valid_docx_upload_returns_201()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();
        var data = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
        var docxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

        using var response = await _client.SendAsync(
            BuildUploadRequest(token, projectId, "contract.docx", data, docxMime));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var doc = await response.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(doc);
        Assert.Equal("contract.docx", doc.OriginalFileName);
        Assert.Equal(docxMime, doc.ContentType);
    }

    // ================================================================
    // Response security
    // ================================================================

    [Fact]
    public async Task Response_does_not_expose_organization_id_or_project_id()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildUploadRequest(token, projectId, "secure.pdf", [0x01]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("organizationId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("org_id", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("projectId", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    // Storage verification
    // ================================================================

    [Fact]
    public async Task Upload_bytes_actually_appear_under_configured_test_storage()
    {
        var (token, orgId, projectId) = await SeedProjectAndLoginAsync();
        var data = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };

        using var response = await _client.SendAsync(
            BuildUploadRequest(token, projectId, "check.pdf", data));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var doc = await response.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(doc);

        var expectedPath = Path.Combine(
            _storageRoot,
            orgId.ToString("N"),
            projectId.ToString("N"),
            doc.Id.ToString("N"));

        Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}");
        var stored = await File.ReadAllBytesAsync(expectedPath);
        Assert.Equal(data, stored);
    }

    // ================================================================
    // List endpoint sees uploaded document
    // ================================================================

    [Fact]
    public async Task Uploaded_metadata_appears_via_get_list_endpoint()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        using var uploadResponse = await _client.SendAsync(
            BuildUploadRequest(token, projectId, "visible.pdf", [0x01]));
        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);

        using var listResponse = await _client.SendAsync(BuildListRequest(token, projectId));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var list = await listResponse.Content.ReadFromJsonAsync<DocumentListResponse>();
        Assert.NotNull(list);
        Assert.Contains(list.Items, d => d.OriginalFileName == "visible.pdf");
    }

    // ================================================================
    // Tenant isolation
    // ================================================================

    [Fact]
    public async Task Upload_to_nonexistent_project_returns_404()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);

        using var response = await _client.SendAsync(
            BuildUploadRequest(token, Guid.NewGuid(), "test.pdf", [0x01]));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Upload_to_cross_tenant_project_returns_404()
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
            BuildUploadRequest(tokenA, projectB, "cross.pdf", [0x01]));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ================================================================
    // Validation failures
    // ================================================================

    [Fact]
    public async Task Missing_file_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        var msg = new HttpRequestMessage(HttpMethod.Post, DocumentsPath(projectId))
        {
            Content = new MultipartFormDataContent(),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Zero_byte_file_returns_400()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildUploadRequest(token, projectId, "empty.pdf", []));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unsupported_extension_returns_422()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildUploadRequest(token, projectId, "malware.exe", [0x01], "application/x-msdownload"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Incompatible_mime_returns_422()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildUploadRequest(token, projectId, "report.pdf", [0x01], "text/html"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_file_returns_413()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        var oversize = (25 * 1024 * 1024) + 1;
        var msg = new HttpRequestMessage(HttpMethod.Post, DocumentsPath(projectId));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(new OversizedStream(oversize));
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(streamContent, "file", "huge.pdf");
        msg.Content = content;

        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    // ================================================================
    // Duplicate filenames
    // ================================================================

    [Fact]
    public async Task Duplicate_filenames_both_succeed()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        using var r1 = await _client.SendAsync(
            BuildUploadRequest(token, projectId, "same.pdf", [0x01]));
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);

        using var r2 = await _client.SendAsync(
            BuildUploadRequest(token, projectId, "same.pdf", [0x02]));
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);

        var d1 = await r1.Content.ReadFromJsonAsync<DocumentResponse>();
        var d2 = await r2.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(d1);
        Assert.NotNull(d2);
        Assert.NotEqual(d1.Id, d2.Id);
    }

    // ================================================================
    // Cross-platform filename handling
    // ================================================================

    [Fact]
    public async Task Windows_style_filename_in_multipart_is_sanitized_to_basename()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildUploadRequest(token, projectId, @"C:\Users\attacker\invoice.pdf", [0x01]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var doc = await response.Content.ReadFromJsonAsync<DocumentResponse>();
        Assert.NotNull(doc);
        Assert.Equal("invoice.pdf", doc.OriginalFileName);
    }

    // ================================================================
    // Tenant identity — must come exclusively from JWT
    // ================================================================

    [Fact]
    public async Task Malicious_organizationId_form_field_is_ignored()
    {
        using var scope = _factory.Services.CreateScope();
        var orgA = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var orgB = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);

        var userA = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, orgA.Id, DefaultPassword, role: "Coordinator");
        var tokenA = await LoginAsync(userA.Email!);
        var projectA = await SeedProjectAsync(scope.ServiceProvider, orgA.Id);

        // Send a legitimate file but sneak in a rogue organizationId form field pointing at OrgB.
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([0x01, 0x02, 0x03]);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "legit.pdf");
        content.Add(new StringContent(orgB.Id.ToString()), "organizationId");

        var msg = new HttpRequestMessage(HttpMethod.Post, DocumentsPath(projectA))
        {
            Content = content,
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);

        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Bytes must land under OrgA's path, never under OrgB's.
        var orgAPath = Path.Combine(_storageRoot, orgA.Id.ToString("N"));
        var orgBPath = Path.Combine(_storageRoot, orgB.Id.ToString("N"));

        Assert.True(Directory.Exists(orgAPath), "OrgA storage directory must exist");
        Assert.False(Directory.Exists(orgBPath), "OrgB storage directory must not exist");
    }

    // ================================================================
    // Existing composite FK test still holds
    // ================================================================

    [Fact]
    public async Task Existing_composite_fk_protection_remains_intact()
    {
        using var scope = _factory.Services.CreateScope();
        var orgA = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var orgB = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);

        var projectBId = await SeedProjectAsync(scope.ServiceProvider, orgB.Id);

        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var badDoc = new OpsFlow.Domain.Documents.Document(
            Guid.NewGuid(), orgA.Id, projectBId, "evil.pdf", "application/pdf", 0,
            DateTimeOffset.UtcNow);
        db.Documents.Add(badDoc);

        _ = await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() =>
            db.SaveChangesAsync());
    }

    // ================================================================
    // Test helpers
    // ================================================================

    private sealed class OversizedStream : Stream
    {
        private long _remaining;
        public OversizedStream(long length) => _remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _remaining;
        public override long Position { get; set; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(count, _remaining);
            Array.Fill(buffer, (byte)0xAA, offset, toRead);
            _remaining -= toRead;
            return toRead;
        }
    }
}
