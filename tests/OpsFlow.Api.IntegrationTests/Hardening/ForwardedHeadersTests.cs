using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using OpsFlow.Api.IntegrationTests.Authentication;
using OpsFlow.Api.IntegrationTests.Infrastructure;

namespace OpsFlow.Api.IntegrationTests.Hardening;

[Collection(SqlServerCollection.Name)]
public sealed class ForwardedHeadersTests : IDisposable
{
    private readonly OpsFlowWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ForwardedHeadersTests(SqlServerFixture fixture)
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
    public async Task Health_endpoint_responds_without_forwarded_headers()
    {
        var response = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Request_with_forwarded_proto_does_not_crash()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Forwarded-For", "198.51.100.1");
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
