using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Api.IntegrationTests.Authentication;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Application.Authorization;
using OpsFlow.Contracts.Authentication;
using OpsFlow.Contracts.Projects;

namespace OpsFlow.Api.IntegrationTests.Authorization;

[Collection(SqlServerCollection.Name)]
public sealed class AuthorizationEndpointTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string LoginPath = "/api/v1/auth/login";
    private const string ProjectsPath = "/api/v1/projects";
    private const string MePath = "/api/v1/auth/me";

    private readonly OpsFlowWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthorizationEndpointTests(SqlServerFixture fixture)
    {
        _factory = new OpsFlowWebApplicationFactory(fixture.ConnectionString);
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
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private async Task<string> SeedAndLoginAsync(string role)
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: role);

        return await LoginAsync(user.Email!);
    }

    private async Task<(string Token, Guid OrgId)> SeedAndLoginWithOrgAsync(string role)
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: role);

        var token = await LoginAsync(user.Email!);
        return (token, org.Id);
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

    private async Task<Guid> CreateProjectAsync(string token, string name = "Test Project")
    {
        var json = JsonSerializer.Serialize(new { Name = name });
        using var msg = new HttpRequestMessage(HttpMethod.Post, ProjectsPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.NotNull(body);
        return body.Id;
    }

    private static string DocumentsPath(Guid projectId) =>
        $"/api/v1/projects/{projectId}/documents";

    private static string ExtractionPath(Guid projectId, Guid documentId) =>
        $"/api/v1/projects/{projectId}/documents/{documentId}/extraction";

    private static string SearchPath(Guid projectId) =>
        $"/api/v1/projects/{projectId}/search";

    private static string AnswerPath(Guid projectId) =>
        $"/api/v1/projects/{projectId}/answer";

    // ================================================================
    // 1. Unauthenticated → 401
    // ================================================================

    [Theory]
    [InlineData("POST", "/api/v1/projects")]
    [InlineData("GET", "/api/v1/projects")]
    [InlineData("GET", "/api/v1/auth/me")]
    public async Task Unauthenticated_request_returns_401(string method, string path)
    {
        var httpMethod = new HttpMethod(method);
        using var msg = new HttpRequestMessage(httpMethod, path);
        if (method == "POST")
        {
            msg.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ================================================================
    // 2. /me — all roles allowed
    // ================================================================

    [Theory]
    [InlineData(OpsFlowRoles.OrganizationAdministrator)]
    [InlineData(OpsFlowRoles.Coordinator)]
    [InlineData(OpsFlowRoles.Technician)]
    [InlineData(OpsFlowRoles.Viewer)]
    public async Task Me_returns_200_for_any_role(string role)
    {
        var token = await SeedAndLoginAsync(role);

        using var msg = new HttpRequestMessage(HttpMethod.Get, MePath);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(msg);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ================================================================
    // 3. List Projects — all roles allowed
    // ================================================================

    [Theory]
    [InlineData(OpsFlowRoles.OrganizationAdministrator)]
    [InlineData(OpsFlowRoles.Coordinator)]
    [InlineData(OpsFlowRoles.Technician)]
    [InlineData(OpsFlowRoles.Viewer)]
    public async Task List_projects_returns_200_for_any_role(string role)
    {
        var token = await SeedAndLoginAsync(role);

        using var msg = new HttpRequestMessage(HttpMethod.Get, ProjectsPath);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(msg);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ================================================================
    // 4. Create Project — Admin and Coordinator allowed
    // ================================================================

    [Theory]
    [InlineData(OpsFlowRoles.OrganizationAdministrator)]
    [InlineData(OpsFlowRoles.Coordinator)]
    public async Task Create_project_returns_201_for_managing_roles(string role)
    {
        var token = await SeedAndLoginAsync(role);

        var json = JsonSerializer.Serialize(new { Name = $"Project by {role}" });
        using var msg = new HttpRequestMessage(HttpMethod.Post, ProjectsPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(msg);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ================================================================
    // 5. Create Project — Technician and Viewer forbidden
    // ================================================================

    [Theory]
    [InlineData(OpsFlowRoles.Technician)]
    [InlineData(OpsFlowRoles.Viewer)]
    public async Task Create_project_returns_403_for_non_managing_roles(string role)
    {
        var token = await SeedAndLoginAsync(role);

        var json = JsonSerializer.Serialize(new { Name = "Forbidden project" });
        using var msg = new HttpRequestMessage(HttpMethod.Post, ProjectsPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(msg);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ================================================================
    // 6. List Documents — all roles allowed
    // ================================================================

    [Theory]
    [InlineData(OpsFlowRoles.OrganizationAdministrator)]
    [InlineData(OpsFlowRoles.Coordinator)]
    [InlineData(OpsFlowRoles.Technician)]
    [InlineData(OpsFlowRoles.Viewer)]
    public async Task List_documents_returns_non_403_for_any_role(string role)
    {
        var (setupToken, _) = await SeedAndLoginWithOrgAsync(OpsFlowRoles.Coordinator);
        var projectId = await CreateProjectAsync(setupToken);

        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: role);
        var token = await LoginAsync(user.Email!);

        using var msg = new HttpRequestMessage(HttpMethod.Get, DocumentsPath(projectId));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(msg);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ================================================================
    // 7. Upload Document — Viewer forbidden
    // ================================================================

    [Fact]
    public async Task Upload_document_returns_403_for_viewer()
    {
        var (setupToken, _) = await SeedAndLoginWithOrgAsync(OpsFlowRoles.Coordinator);
        var projectId = await CreateProjectAsync(setupToken);

        var viewerToken = await SeedAndLoginAsync(OpsFlowRoles.Viewer);

        using var msg = new HttpRequestMessage(HttpMethod.Post, DocumentsPath(projectId));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        msg.Content = new MultipartFormDataContent
        {
            { new ByteArrayContent("test"u8.ToArray()), "file", "test.txt" }
        };
        using var response = await _client.SendAsync(msg);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ================================================================
    // 8. Upload Document — Contributing roles allowed
    // ================================================================

    [Theory]
    [InlineData(OpsFlowRoles.OrganizationAdministrator)]
    [InlineData(OpsFlowRoles.Coordinator)]
    [InlineData(OpsFlowRoles.Technician)]
    public async Task Upload_document_returns_non_403_for_contributing_roles(string role)
    {
        var token = await SeedAndLoginAsync(role);

        var dummyProjectId = Guid.NewGuid();
        using var msg = new HttpRequestMessage(HttpMethod.Post, DocumentsPath(dummyProjectId));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        msg.Content = new MultipartFormDataContent
        {
            { new ByteArrayContent("valid text"u8.ToArray()), "file", "test.txt" }
        };
        using var response = await _client.SendAsync(msg);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ================================================================
    // 9. Extract Text — Viewer forbidden
    // ================================================================

    [Fact]
    public async Task Extract_text_returns_403_for_viewer()
    {
        var viewerToken = await SeedAndLoginAsync(OpsFlowRoles.Viewer);
        var dummyProjectId = Guid.NewGuid();
        var dummyDocumentId = Guid.NewGuid();

        using var msg = new HttpRequestMessage(
            HttpMethod.Post, ExtractionPath(dummyProjectId, dummyDocumentId));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var response = await _client.SendAsync(msg);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ================================================================
    // 10. Get Extraction — all roles allowed (read-only)
    // ================================================================

    [Theory]
    [InlineData(OpsFlowRoles.OrganizationAdministrator)]
    [InlineData(OpsFlowRoles.Coordinator)]
    [InlineData(OpsFlowRoles.Technician)]
    [InlineData(OpsFlowRoles.Viewer)]
    public async Task Get_extraction_returns_non_403_for_any_role(string role)
    {
        var token = await SeedAndLoginAsync(role);
        var dummyProjectId = Guid.NewGuid();
        var dummyDocumentId = Guid.NewGuid();

        using var msg = new HttpRequestMessage(
            HttpMethod.Get, ExtractionPath(dummyProjectId, dummyDocumentId));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(msg);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ================================================================
    // 11. Search — all roles allowed (read-like)
    // ================================================================

    [Theory]
    [InlineData(OpsFlowRoles.OrganizationAdministrator)]
    [InlineData(OpsFlowRoles.Coordinator)]
    [InlineData(OpsFlowRoles.Technician)]
    [InlineData(OpsFlowRoles.Viewer)]
    public async Task Search_returns_non_403_for_any_role(string role)
    {
        var token = await SeedAndLoginAsync(role);
        var dummyProjectId = Guid.NewGuid();

        var json = JsonSerializer.Serialize(new { QueryText = "test query" });
        using var msg = new HttpRequestMessage(HttpMethod.Post, SearchPath(dummyProjectId))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(msg);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ================================================================
    // 12. Answer — all roles allowed (read-like)
    // ================================================================

    [Theory]
    [InlineData(OpsFlowRoles.OrganizationAdministrator)]
    [InlineData(OpsFlowRoles.Coordinator)]
    [InlineData(OpsFlowRoles.Technician)]
    [InlineData(OpsFlowRoles.Viewer)]
    public async Task Answer_returns_non_403_for_any_role(string role)
    {
        var token = await SeedAndLoginAsync(role);
        var dummyProjectId = Guid.NewGuid();

        var json = JsonSerializer.Serialize(new { Question = "test question" });
        using var msg = new HttpRequestMessage(HttpMethod.Post, AnswerPath(dummyProjectId))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(msg);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ================================================================
    // 13. 401 vs 403 distinction
    // ================================================================

    [Fact]
    public async Task Unauthenticated_returns_401_not_403()
    {
        var json = JsonSerializer.Serialize(new { Name = "Test" });
        using var msg = new HttpRequestMessage(HttpMethod.Post, ProjectsPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await _client.SendAsync(msg);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_insufficient_role_returns_403_not_401()
    {
        var viewerToken = await SeedAndLoginAsync(OpsFlowRoles.Viewer);

        var json = JsonSerializer.Serialize(new { Name = "Forbidden" });
        using var msg = new HttpRequestMessage(HttpMethod.Post, ProjectsPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var response = await _client.SendAsync(msg);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ================================================================
    // 14. Cross-tenant — valid role still cannot access other org
    // ================================================================

    [Fact]
    public async Task Admin_role_cannot_access_other_organization_projects()
    {
        using var scope = _factory.Services.CreateScope();
        var orgA = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var orgB = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);

        var adminA = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, orgA.Id, DefaultPassword,
            role: OpsFlowRoles.OrganizationAdministrator);
        var adminB = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, orgB.Id, DefaultPassword,
            role: OpsFlowRoles.OrganizationAdministrator);

        var tokenA = await LoginAsync(adminA.Email!);
        var tokenB = await LoginAsync(adminB.Email!);

        await CreateProjectAsync(tokenB, "Org B Secret");

        using var listMsg = new HttpRequestMessage(HttpMethod.Get, ProjectsPath);
        listMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        using var listResponse = await _client.SendAsync(listMsg);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<ProjectListResponse>();
        Assert.NotNull(list);
        Assert.DoesNotContain(list.Items, p => p.Name == "Org B Secret");
    }

    // ================================================================
    // 15. Role claim drives authorization
    // ================================================================

    [Fact]
    public async Task User_with_no_role_gets_403_on_policy_protected_endpoint()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: null);
        var token = await LoginAsync(user.Email!);

        var json = JsonSerializer.Serialize(new { Name = "Test" });
        using var msg = new HttpRequestMessage(HttpMethod.Post, ProjectsPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(msg);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
