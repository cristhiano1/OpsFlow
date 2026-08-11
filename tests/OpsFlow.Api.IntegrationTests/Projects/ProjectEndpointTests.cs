using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Api.IntegrationTests.Authentication;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Contracts.Authentication;
using OpsFlow.Contracts.Projects;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Projects;

[Collection(SqlServerCollection.Name)]
public sealed class ProjectEndpointTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string ProjectsPath = "/api/v1/projects";
    private const string LoginPath = "/api/v1/auth/login";

    private readonly OpsFlowWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProjectEndpointTests(SqlServerFixture fixture)
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

    private async Task<string> SeedAndLoginAsync(string? role = "Coordinator")
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: role);

        return await LoginAsync(user.Email!);
    }

    private async Task<(string Token, Guid OrgId)> SeedAndLoginWithOrgAsync(
        string? role = "Coordinator")
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

    private static HttpRequestMessage BuildCreateRequest(string token, object? body)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, ProjectsPath)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return msg;
    }

    private static HttpRequestMessage BuildListRequest(string token)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, ProjectsPath);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return msg;
    }

    // ================================================================
    // Unauthenticated
    // ================================================================

    [Fact]
    public async Task Create_without_bearer_returns_401()
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, ProjectsPath)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { Name = "Test" }), Encoding.UTF8, "application/json"),
        };
        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_without_bearer_returns_401()
    {
        using var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, ProjectsPath));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ================================================================
    // POST /api/v1/projects — happy path
    // ================================================================

    [Fact]
    public async Task Create_valid_project_returns_201_with_project_response()
    {
        var token = await SeedAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildCreateRequest(token, new { Name = "Alpha", Description = "A test project" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var project = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.NotNull(project);
        Assert.NotEqual(Guid.Empty, project.Id);
        Assert.Equal("Alpha", project.Name);
        Assert.Equal("A test project", project.Description);
        Assert.True(project.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Create_project_response_does_not_contain_organization_id()
    {
        var token = await SeedAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildCreateRequest(token, new { Name = "NoOrgLeak" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("organizationId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("org_id", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_project_is_persisted_and_visible_in_list()
    {
        var token = await SeedAndLoginAsync();

        using var createResponse = await _client.SendAsync(
            BuildCreateRequest(token, new { Name = "Persisted" }));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var listResponse = await _client.SendAsync(BuildListRequest(token));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var list = await listResponse.Content.ReadFromJsonAsync<ProjectListResponse>();
        Assert.NotNull(list);
        Assert.Contains(list.Items, p => p.Name == "Persisted");
    }

    [Fact]
    public async Task Create_project_with_null_description_succeeds()
    {
        var token = await SeedAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildCreateRequest(token, new { Name = "NoDesc" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var project = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.NotNull(project);
        Assert.Null(project.Description);
    }

    // ================================================================
    // POST — validation failures (400)
    // ================================================================

    [Fact]
    public async Task Create_with_null_body_returns_400()
    {
        var token = await SeedAndLoginAsync();

        var msg = new HttpRequestMessage(HttpMethod.Post, ProjectsPath)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_with_empty_name_returns_400()
    {
        var token = await SeedAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildCreateRequest(token, new { Name = "" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_with_missing_name_returns_400()
    {
        var token = await SeedAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildCreateRequest(token, new { Description = "no name" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_with_name_exceeding_200_chars_returns_400()
    {
        var token = await SeedAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildCreateRequest(token, new { Name = new string('X', 201) }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_with_description_exceeding_2000_chars_returns_400()
    {
        var token = await SeedAndLoginAsync();

        using var response = await _client.SendAsync(
            BuildCreateRequest(token, new { Name = "Valid", Description = new string('Y', 2001) }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ================================================================
    // Tenant isolation
    // ================================================================

    [Fact]
    public async Task Northwind_user_cannot_see_Contoso_projects()
    {
        // Seed two separate orgs, each with a user
        using var scope = _factory.Services.CreateScope();
        var northwindOrg = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var contosoOrg = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);

        var northwindUser = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, northwindOrg.Id, DefaultPassword, role: "Coordinator");
        var contosoUser = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, contosoOrg.Id, DefaultPassword, role: "Coordinator");

        var northwindToken = await LoginAsync(northwindUser.Email!);
        var contosoToken = await LoginAsync(contosoUser.Email!);

        // Contoso creates a project
        using var createResponse = await _client.SendAsync(
            BuildCreateRequest(contosoToken, new { Name = "Contoso Secret" }));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Northwind lists projects — must NOT see Contoso's project
        using var listResponse = await _client.SendAsync(BuildListRequest(northwindToken));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var list = await listResponse.Content.ReadFromJsonAsync<ProjectListResponse>();
        Assert.NotNull(list);
        Assert.DoesNotContain(list.Items, p => p.Name == "Contoso Secret");
    }

    [Fact]
    public async Task Contoso_user_cannot_see_Northwind_projects()
    {
        using var scope = _factory.Services.CreateScope();
        var northwindOrg = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var contosoOrg = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);

        var northwindUser = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, northwindOrg.Id, DefaultPassword, role: "Coordinator");
        var contosoUser = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, contosoOrg.Id, DefaultPassword, role: "Coordinator");

        var northwindToken = await LoginAsync(northwindUser.Email!);
        var contosoToken = await LoginAsync(contosoUser.Email!);

        // Northwind creates a project
        using var createResponse = await _client.SendAsync(
            BuildCreateRequest(northwindToken, new { Name = "Northwind Internal" }));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Contoso lists projects — must NOT see Northwind's project
        using var listResponse = await _client.SendAsync(BuildListRequest(contosoToken));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var list = await listResponse.Content.ReadFromJsonAsync<ProjectListResponse>();
        Assert.NotNull(list);
        Assert.DoesNotContain(list.Items, p => p.Name == "Northwind Internal");
    }

    [Fact]
    public async Task Malicious_organizationId_in_POST_body_is_ignored()
    {
        using var scope = _factory.Services.CreateScope();
        var attackerOrg = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var victimOrg = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);

        var attacker = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, attackerOrg.Id, DefaultPassword, role: "Coordinator");
        var victim = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, victimOrg.Id, DefaultPassword, role: "Coordinator");

        var attackerToken = await LoginAsync(attacker.Email!);
        var victimToken = await LoginAsync(victim.Email!);

        // Attacker sends organizationId in POST body pointing to victim's org
        var maliciousBody = new
        {
            Name = "Injected",
            OrganizationId = victimOrg.Id,
        };
        using var createResponse = await _client.SendAsync(
            BuildCreateRequest(attackerToken, maliciousBody));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Victim's project list must NOT contain the injected project
        using var victimList = await _client.SendAsync(BuildListRequest(victimToken));
        var victimProjects = await victimList.Content.ReadFromJsonAsync<ProjectListResponse>();
        Assert.NotNull(victimProjects);
        Assert.DoesNotContain(victimProjects.Items, p => p.Name == "Injected");

        // Attacker's project list MUST contain it (project belongs to attacker's org)
        using var attackerList = await _client.SendAsync(BuildListRequest(attackerToken));
        var attackerProjects = await attackerList.Content.ReadFromJsonAsync<ProjectListResponse>();
        Assert.NotNull(attackerProjects);
        Assert.Contains(attackerProjects.Items, p => p.Name == "Injected");
    }

    // ================================================================
    // GET /api/v1/projects — list behavior
    // ================================================================

    [Fact]
    public async Task List_returns_200_with_empty_items_when_no_projects()
    {
        var token = await SeedAndLoginAsync();

        using var response = await _client.SendAsync(BuildListRequest(token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<ProjectListResponse>();
        Assert.NotNull(list);
        Assert.Empty(list.Items);
    }

    [Fact]
    public async Task List_returns_projects_ordered_by_newest_first()
    {
        var token = await SeedAndLoginAsync();

        using var r1 = await _client.SendAsync(
            BuildCreateRequest(token, new { Name = "First" }));
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);

        using var r2 = await _client.SendAsync(
            BuildCreateRequest(token, new { Name = "Second" }));
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);

        using var r3 = await _client.SendAsync(
            BuildCreateRequest(token, new { Name = "Third" }));
        Assert.Equal(HttpStatusCode.Created, r3.StatusCode);

        using var listResponse = await _client.SendAsync(BuildListRequest(token));
        var list = await listResponse.Content.ReadFromJsonAsync<ProjectListResponse>();
        Assert.NotNull(list);
        Assert.True(list.Items.Count >= 3);

        var names = list.Items.Select(p => p.Name).ToList();
        var firstIdx = names.IndexOf("First");
        var secondIdx = names.IndexOf("Second");
        var thirdIdx = names.IndexOf("Third");

        Assert.True(thirdIdx < secondIdx, "Third should appear before Second (newest first)");
        Assert.True(secondIdx < firstIdx, "Second should appear before First (newest first)");
    }

    [Fact]
    public async Task List_response_does_not_contain_organization_id()
    {
        var token = await SeedAndLoginAsync();
        using var createResponse = await _client.SendAsync(
            BuildCreateRequest(token, new { Name = "OrgCheck" }));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var listResponse = await _client.SendAsync(BuildListRequest(token));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var raw = await listResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("organizationId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("org_id", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    // Database-level persistence
    // ================================================================

    [Fact]
    public async Task Created_project_is_in_database_with_correct_organization_id()
    {
        var (token, orgId) = await SeedAndLoginWithOrgAsync();

        using var createResponse = await _client.SendAsync(
            BuildCreateRequest(token, new { Name = "DbCheck" }));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.NotNull(created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var project = await db.Projects.FindAsync(created.Id);
        Assert.NotNull(project);
        Assert.Equal(orgId, project.OrganizationId);
        Assert.Equal("DbCheck", project.Name);
    }
}
