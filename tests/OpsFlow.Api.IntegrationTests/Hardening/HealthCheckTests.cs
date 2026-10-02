using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using OpsFlow.Api.IntegrationTests.Authentication;
using OpsFlow.Api.IntegrationTests.Infrastructure;

namespace OpsFlow.Api.IntegrationTests.Hardening;

[Collection(SqlServerCollection.Name)]
public sealed class HealthCheckTests : IDisposable
{
    private readonly OpsFlowWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public HealthCheckTests(SqlServerFixture fixture)
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

    [Fact]
    public async Task Live_returns_200_without_authentication()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_returns_200_with_healthy_database()
    {
        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Live_response_contains_only_status_text()
    {
        var response = await _client.GetAsync("/health/live");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task Ready_response_exposes_no_sensitive_information()
    {
        var response = await _client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("Healthy", body);
        Assert.DoesNotContain("Server", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
    }
}
