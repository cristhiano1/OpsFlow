using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Api.IntegrationTests.Authentication;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Contracts.Analytics;
using OpsFlow.Contracts.Authentication;
using OpsFlow.Infrastructure.Observability;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Analytics;

[Collection(SqlServerCollection.Name)]
public sealed class AnalyticsEndpointTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string SummaryPath = "/api/v1/analytics/summary";
    private const string LoginPath = "/api/v1/auth/login";

    private readonly OpsFlowWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AnalyticsEndpointTests(SqlServerFixture fixture)
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

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task<(string Token, Guid OrgId)> SeedAndLoginAsync(string? role = "Coordinator")
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword, role: role);

        var json = JsonSerializer.Serialize(new { email = user.Email, password = DefaultPassword });
        using var msg = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var resp = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);
        return (body.AccessToken, org.Id);
    }

    private async Task InsertMetricsAsync(Guid organizationId, int count)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();

        for (var i = 0; i < count; i++)
        {
            db.RagMetricEntries.Add(new RagMetricEntry
            {
                OrganizationId = organizationId,
                RecordedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                Outcome = "Answered",
                RetrievalMode = "Hybrid",
                TotalDurationSeconds = 1.0,
                RetrievalDurationSeconds = 0.3,
                GenerationDurationSeconds = 0.6,
                SelectedEvidenceCount = 4,
            });
        }

        await db.SaveChangesAsync();
    }

    // ── tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_without_bearer_returns_401()
    {
        using var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, SummaryPath));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Summary_with_valid_bearer_returns_200_with_expected_shape()
    {
        var (token, _) = await SeedAndLoginAsync();

        var msg = new HttpRequestMessage(HttpMethod.Get, SummaryPath);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = await response.Content.ReadFromJsonAsync<AnalyticsSummaryResponse>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(summary);
        Assert.True(summary.ProjectCount >= 0);
        Assert.True(summary.DocumentCount >= 0);
        Assert.NotNull(summary.RagPipeline);
        Assert.True(summary.RagPipeline.TotalRequests >= 0);
        Assert.NotNull(summary.RagPipeline.OutcomeBreakdown);
        Assert.NotNull(summary.RagPipeline.DailyTrend);
    }

    [Fact]
    public async Task Summary_returns_only_metrics_scoped_to_callers_organization()
    {
        // Seed 5 metrics for org A
        var (_, orgAId) = await SeedAndLoginAsync();
        await InsertMetricsAsync(orgAId, 5);

        // Login as org B (no metrics)
        var (tokenB, _) = await SeedAndLoginAsync();

        var msgB = new HttpRequestMessage(HttpMethod.Get, SummaryPath);
        msgB.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        using var respB = await _client.SendAsync(msgB);
        Assert.Equal(HttpStatusCode.OK, respB.StatusCode);

        var summaryB = await respB.Content.ReadFromJsonAsync<AnalyticsSummaryResponse>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(summaryB);
        Assert.Equal(0, summaryB.RagPipeline.TotalRequests);
    }

    [Fact]
    public async Task Summary_counts_correct_number_of_rag_requests_for_organization()
    {
        var (token, orgId) = await SeedAndLoginAsync();
        await InsertMetricsAsync(orgId, 7);

        var msg = new HttpRequestMessage(HttpMethod.Get, SummaryPath);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = await response.Content.ReadFromJsonAsync<AnalyticsSummaryResponse>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(summary);
        Assert.Equal(7, summary.RagPipeline.TotalRequests);
        Assert.Equal(7, summary.RagPipeline.AnsweredCount);
    }
}
