using Microsoft.AspNetCore.Mvc.Testing;
using OpsFlow.Api.IntegrationTests.Authentication;
using OpsFlow.Api.IntegrationTests.Infrastructure;

namespace OpsFlow.Api.IntegrationTests.Hardening;

[Collection(SqlServerCollection.Name)]
public sealed class CorsTests : IDisposable
{
    private const string AllowedOrigin = "http://localhost:5173";
    private const string DisallowedOrigin = "https://evil.example.com";

    private readonly OpsFlowWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CorsTests(SqlServerFixture fixture)
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
    public async Task Allowed_origin_receives_cors_headers()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", AllowedOrigin);

        var response = await _client.SendAsync(request);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        var origin = response.Headers.GetValues("Access-Control-Allow-Origin").Single();
        Assert.Equal(AllowedOrigin, origin);
    }

    [Fact]
    public async Task Allowed_origin_receives_credentials_header()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", AllowedOrigin);

        var response = await _client.SendAsync(request);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Credentials"));
        var value = response.Headers.GetValues("Access-Control-Allow-Credentials").Single();
        Assert.Equal("true", value);
    }

    [Fact]
    public async Task Disallowed_origin_does_not_receive_cors_headers()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", DisallowedOrigin);

        var response = await _client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Preflight_returns_cors_headers_for_allowed_origin()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        request.Headers.Add("Origin", AllowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "Content-Type");

        var response = await _client.SendAsync(request);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.True(response.Headers.Contains("Access-Control-Allow-Methods"));
        Assert.True(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Authorization_header_is_allowed_in_cors()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/projects");
        request.Headers.Add("Origin", AllowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "Authorization");

        var response = await _client.SendAsync(request);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Headers"));
        var allowedHeaders = string.Join(",",
            response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("Authorization", allowedHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Response_never_contains_wildcard_origin()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", AllowedOrigin);

        var response = await _client.SendAsync(request);

        if (response.Headers.Contains("Access-Control-Allow-Origin"))
        {
            var origin = response.Headers.GetValues("Access-Control-Allow-Origin").Single();
            Assert.NotEqual("*", origin);
        }
    }
}
