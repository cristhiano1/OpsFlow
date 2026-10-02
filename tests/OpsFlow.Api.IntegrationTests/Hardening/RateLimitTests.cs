using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpsFlow.Api.IntegrationTests.Authentication;
using OpsFlow.Api.IntegrationTests.Infrastructure;

namespace OpsFlow.Api.IntegrationTests.Hardening;

[Collection(SqlServerCollection.Name)]
public sealed class RateLimitTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly List<IDisposable> _disposables = [];

    public RateLimitTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }
    }

    private HttpClient CreateClientWithOverrides(Dictionary<string, string?> overrides)
    {
        var factory = new OpsFlowWebApplicationFactory(_fixture.ConnectionString, overrides);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
        _disposables.Add(client);
        _disposables.Add(factory);
        return client;
    }

    [Fact]
    public async Task Requests_below_auth_limit_succeed()
    {
        var client = CreateClientWithOverrides(new()
        {
            ["RateLimiting:Auth:PermitLimit"] = "5",
        });

        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsync("/api/v1/auth/login",
                JsonContent.Create(new { email = "test@test.com", password = "wrong" }));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }

    [Fact]
    public async Task Requests_above_auth_limit_return_429()
    {
        var client = CreateClientWithOverrides(new()
        {
            ["RateLimiting:Auth:PermitLimit"] = "2",
        });

        for (var i = 0; i < 2; i++)
        {
            await client.PostAsync("/api/v1/auth/login",
                JsonContent.Create(new { email = "test@test.com", password = "wrong" }));
        }

        var blocked = await client.PostAsync("/api/v1/auth/login",
            JsonContent.Create(new { email = "test@test.com", password = "wrong" }));
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    [Fact]
    public async Task Rate_limit_429_includes_retry_after_header()
    {
        var client = CreateClientWithOverrides(new()
        {
            ["RateLimiting:Auth:PermitLimit"] = "1",
        });

        await client.PostAsync("/api/v1/auth/login",
            JsonContent.Create(new { email = "test@test.com", password = "wrong" }));

        var blocked = await client.PostAsync("/api/v1/auth/login",
            JsonContent.Create(new { email = "test@test.com", password = "wrong" }));

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Health_endpoint_is_not_rate_limited()
    {
        var client = CreateClientWithOverrides(new()
        {
            ["RateLimiting:Auth:PermitLimit"] = "1",
            ["RateLimiting:Api:PermitLimit"] = "1",
            ["RateLimiting:Rag:PermitLimit"] = "1",
            ["RateLimiting:Upload:PermitLimit"] = "1",
        });

        for (var i = 0; i < 10; i++)
        {
            var response = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Rag_expensive_policy_limits_search_endpoint()
    {
        var client = CreateClientWithOverrides(new()
        {
            ["RateLimiting:Rag:PermitLimit"] = "1",
        });

        var projectId = Guid.NewGuid();
        var searchUrl = $"/api/v1/projects/{projectId}/search";
        var body = JsonContent.Create(new { queryText = "test", topK = 5 });

        // First request: 401 expected (no token) but NOT 429
        var response1 = await client.PostAsync(searchUrl, body);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, response1.StatusCode);

        // Second request: rate limited
        var response2 = await client.PostAsync(searchUrl,
            JsonContent.Create(new { queryText = "test", topK = 5 }));
        Assert.Equal(HttpStatusCode.TooManyRequests, response2.StatusCode);
    }

    [Fact]
    public async Task Upload_policy_limits_document_upload()
    {
        var client = CreateClientWithOverrides(new()
        {
            ["RateLimiting:Upload:PermitLimit"] = "1",
        });

        var projectId = Guid.NewGuid();
        var uploadUrl = $"/api/v1/projects/{projectId}/documents";

        // First request: 401 expected (no token) but NOT 429
        var content1 = new MultipartFormDataContent
        {
            { new StringContent("test"), "file", "test.txt" },
        };
        var response1 = await client.PostAsync(uploadUrl, content1);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, response1.StatusCode);

        // Second request: rate limited
        var content2 = new MultipartFormDataContent
        {
            { new StringContent("test"), "file", "test.txt" },
        };
        var response2 = await client.PostAsync(uploadUrl, content2);
        Assert.Equal(HttpStatusCode.TooManyRequests, response2.StatusCode);
    }

    [Fact]
    public async Task Different_rate_limit_partitions_are_independent()
    {
        var client = CreateClientWithOverrides(new()
        {
            ["RateLimiting:Auth:PermitLimit"] = "1",
            ["RateLimiting:Api:PermitLimit"] = "1",
        });

        // Exhaust auth limit
        await client.PostAsync("/api/v1/auth/login",
            JsonContent.Create(new { email = "test@test.com", password = "wrong" }));

        // API endpoint should still work (different policy, same IP but different partition key prefix)
        var response = await client.GetAsync("/api/v1/projects");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
    }
}
