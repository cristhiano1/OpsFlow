using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Api.IntegrationTests.Authentication;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Contracts.Authentication;
using OpsFlow.Contracts.Documents;
using OpsFlow.Domain.Documents;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Projects;

[Collection(SqlServerCollection.Name)]
public sealed class DocumentEndpointTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string LoginPath = "/api/v1/auth/login";

    private readonly OpsFlowWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DocumentEndpointTests(SqlServerFixture fixture)
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

    private static async Task<Document> SeedDocumentAsync(
        IServiceProvider scopeServices,
        Guid orgId,
        Guid projectId,
        string fileName = "report.pdf",
        string contentType = "application/pdf",
        long sizeBytes = 1024,
        DateTimeOffset? createdAt = null,
        Guid? id = null)
    {
        var db = scopeServices.GetRequiredService<OpsFlowDbContext>();
        var doc = new Document(
            id ?? Guid.NewGuid(), orgId, projectId, fileName, contentType, sizeBytes,
            createdAt ?? DateTimeOffset.UtcNow);
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
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
    public async Task List_without_bearer_returns_401()
    {
        using var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, DocumentsPath(Guid.NewGuid())));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ================================================================
    // 200 — empty
    // ================================================================

    [Fact]
    public async Task Existing_project_with_no_documents_returns_200_with_empty_items()
    {
        var (token, _, projectId) = await SeedProjectAndLoginAsync();

        using var response = await _client.SendAsync(BuildListRequest(token, projectId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<DocumentListResponse>();
        Assert.NotNull(list);
        Assert.Empty(list.Items);
    }

    // ================================================================
    // 200 — with documents
    // ================================================================

    [Fact]
    public async Task Existing_project_with_documents_returns_correct_metadata()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);
        var projectId = await SeedProjectAsync(scope.ServiceProvider, org.Id);

        var doc = await SeedDocumentAsync(
            scope.ServiceProvider, org.Id, projectId,
            "contract.pdf", "application/pdf", 8192);

        using var response = await _client.SendAsync(BuildListRequest(token, projectId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<DocumentListResponse>();
        Assert.NotNull(list);
        _ = Assert.Single(list.Items);
        Assert.Equal(doc.Id, list.Items[0].Id);
        Assert.Equal("contract.pdf", list.Items[0].OriginalFileName);
        Assert.Equal("application/pdf", list.Items[0].ContentType);
        Assert.Equal(8192, list.Items[0].SizeBytes);
    }

    // ================================================================
    // Deterministic ordering
    // ================================================================

    [Fact]
    public async Task Documents_are_returned_newest_first()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);
        var projectId = await SeedProjectAsync(scope.ServiceProvider, org.Id);

        var base_ = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
        await SeedDocumentAsync(scope.ServiceProvider, org.Id, projectId, "first.pdf", "application/pdf", 1, base_);
        await SeedDocumentAsync(scope.ServiceProvider, org.Id, projectId, "second.pdf", "application/pdf", 2, base_.AddSeconds(1));
        await SeedDocumentAsync(scope.ServiceProvider, org.Id, projectId, "third.pdf", "application/pdf", 3, base_.AddSeconds(2));

        using var response = await _client.SendAsync(BuildListRequest(token, projectId));
        var list = await response.Content.ReadFromJsonAsync<DocumentListResponse>();
        Assert.NotNull(list);
        Assert.True(list.Items.Count >= 3);

        var names = list.Items.Select(d => d.OriginalFileName).ToList();
        Assert.True(names.IndexOf("third.pdf") < names.IndexOf("second.pdf"), "third should be before second");
        Assert.True(names.IndexOf("second.pdf") < names.IndexOf("first.pdf"), "second should be before first");
    }

    // ================================================================
    // Scoping — same org, different projects
    // ================================================================

    [Fact]
    public async Task Documents_from_another_project_in_same_org_are_not_returned()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);

        var projectA = await SeedProjectAsync(scope.ServiceProvider, org.Id);
        var projectB = await SeedProjectAsync(scope.ServiceProvider, org.Id);

        await SeedDocumentAsync(scope.ServiceProvider, org.Id, projectB, "b-only.pdf", "application/pdf", 1);

        using var response = await _client.SendAsync(BuildListRequest(token, projectA));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<DocumentListResponse>();
        Assert.NotNull(list);
        Assert.DoesNotContain(list.Items, d => d.OriginalFileName == "b-only.pdf");
    }

    // ================================================================
    // Tenant isolation — 404 for cross-tenant project
    // ================================================================

    [Fact]
    public async Task Cross_tenant_project_returns_404()
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

        using var response = await _client.SendAsync(BuildListRequest(tokenA, projectB));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ================================================================
    // Nonexistent project — 404
    // ================================================================

    [Fact]
    public async Task Nonexistent_project_returns_404()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);

        using var response = await _client.SendAsync(BuildListRequest(token, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ================================================================
    // Tenant isolation — Northwind must not see Contoso documents
    // ================================================================

    [Fact]
    public async Task Northwind_user_cannot_see_Contoso_document_metadata()
    {
        using var scope = _factory.Services.CreateScope();
        var northwindOrg = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var contosoOrg = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);

        var northwindUser = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, northwindOrg.Id, DefaultPassword, role: "Coordinator");
        await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, contosoOrg.Id, DefaultPassword, role: "Coordinator");

        var northwindToken = await LoginAsync(northwindUser.Email!);

        var northwindProject = await SeedProjectAsync(scope.ServiceProvider, northwindOrg.Id);
        var contosoProject = await SeedProjectAsync(scope.ServiceProvider, contosoOrg.Id);
        await SeedDocumentAsync(scope.ServiceProvider, contosoOrg.Id, contosoProject, "contoso-secret.pdf", "application/pdf", 1);

        // Northwind requests Contoso's project — must get 404
        using var response = await _client.SendAsync(BuildListRequest(northwindToken, contosoProject));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Northwind's own project has no documents
        using var ownResponse = await _client.SendAsync(BuildListRequest(northwindToken, northwindProject));
        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        var list = await ownResponse.Content.ReadFromJsonAsync<DocumentListResponse>();
        Assert.NotNull(list);
        Assert.DoesNotContain(list.Items, d => d.OriginalFileName == "contoso-secret.pdf");
    }

    // ================================================================
    // Response must not expose tenant or project identifiers
    // ================================================================

    [Fact]
    public async Task Response_does_not_expose_organization_id_or_project_id()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);
        var projectId = await SeedProjectAsync(scope.ServiceProvider, org.Id);
        await SeedDocumentAsync(scope.ServiceProvider, org.Id, projectId, "check.pdf", "application/pdf", 1);

        using var response = await _client.SendAsync(BuildListRequest(token, projectId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("organizationId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("org_id", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("projectId", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    // Database integrity — composite FK rejects cross-tenant document
    // ================================================================

    [Fact]
    public async Task Composite_fk_rejects_document_referencing_project_from_different_organization()
    {
        using var scope = _factory.Services.CreateScope();
        var orgA = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var orgB = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);

        var projectBId = await SeedProjectAsync(scope.ServiceProvider, orgB.Id);

        // Attempt to insert a Document with OrganizationId = orgA but ProjectId = projectB
        // This violates the composite FK: Document(ProjectId, OrganizationId) -> Project(Id, OrganizationId)
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var badDoc = new Document(
            Guid.NewGuid(), orgA.Id, projectBId, "evil.pdf", "application/pdf", 0,
            DateTimeOffset.UtcNow);
        db.Documents.Add(badDoc);

        _ = await Assert.ThrowsAsync<DbUpdateException>(() =>
            db.SaveChangesAsync());
    }

    // ================================================================
    // Secondary ordering — same CreatedAt, Id DESC tie-break
    // ================================================================

    [Fact]
    public async Task Documents_with_same_created_at_are_ordered_by_id_descending()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: "Coordinator");
        var token = await LoginAsync(user.Email!);
        var projectId = await SeedProjectAsync(scope.ServiceProvider, org.Id);

        // Two GUIDs that differ only in their final byte segment.
        // SQL Server sorts uniqueidentifier by the last 6 bytes first, so
        // ...0002 > ...0001, meaning Id DESC places higherId before lowerId.
        var lowerId = new Guid("00000000-0000-0000-0000-000000000001");
        var higherId = new Guid("00000000-0000-0000-0000-000000000002");
        var fixedTime = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

        await SeedDocumentAsync(scope.ServiceProvider, org.Id, projectId,
            "lower-id.pdf", "application/pdf", 1, fixedTime, id: lowerId);
        await SeedDocumentAsync(scope.ServiceProvider, org.Id, projectId,
            "higher-id.pdf", "application/pdf", 2, fixedTime, id: higherId);

        using var response = await _client.SendAsync(BuildListRequest(token, projectId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<DocumentListResponse>();
        Assert.NotNull(list);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(higherId, list.Items[0].Id);
        Assert.Equal(lowerId, list.Items[1].Id);
    }
}
