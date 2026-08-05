using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Domain.Organizations;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Authentication;

/// <summary>
/// Deterministic HTTP-level concurrency coverage for POST /api/v1/auth/logout.
/// Uses xUnit Barriers to force the racing requests to reach the endpoint
/// simultaneously. No Thread.Sleep, no wall-clock coordination.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class LogoutConcurrencyTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string LoginPath = "/api/v1/auth/login";
    private const string LogoutPath = "/api/v1/auth/logout";
    private const string RefreshPath = "/api/v1/auth/refresh";
    private const string CookieName = "opsflow_refresh_token";

    private readonly OpsFlowWebApplicationFactory _factory;

    public LogoutConcurrencyTests(SqlServerFixture fixture)
    {
        _factory = new OpsFlowWebApplicationFactory(fixture.ConnectionString);
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

    private async Task<(ApplicationUser User, Organization Org)> SeedActiveUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(scope.ServiceProvider, org.Id, DefaultPassword);
        return (user, org);
    }

    private static async Task<string> LoginAndReturnCookieValueAsync(HttpClient client, string email)
    {
        var json = JsonSerializer.Serialize(new { email, password = DefaultPassword });
        using var msg = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ExtractCookieValue(response)!;
    }

    private static HttpRequestMessage BuildLogoutRequest(string cookieValue)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, LogoutPath);
        msg.Headers.Add("Cookie", $"{CookieName}={cookieValue}");
        return msg;
    }

    private static HttpRequestMessage BuildRefreshRequest(string cookieValue)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, RefreshPath);
        msg.Headers.Add("Cookie", $"{CookieName}={cookieValue}");
        return msg;
    }

    private static string? ExtractCookieValue(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }
        var prefix = CookieName + "=";
        foreach (var cookie in cookies)
        {
            if (!cookie.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var end = cookie.IndexOf(';', prefix.Length);
            return end < 0 ? cookie[prefix.Length..] : cookie[prefix.Length..end];
        }
        return null;
    }

    private static async Task<HttpResponseMessage[]> RunInParallelAsync(
        int count, Func<int, Task<HttpResponseMessage>> requestFactory)
    {
        var barrier = new Barrier(count);
        var tasks = new Task<HttpResponseMessage>[count];
        for (var i = 0; i < count; i++)
        {
            var index = i;
            tasks[i] = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await requestFactory(index);
            });
        }
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Parallel_logouts_of_same_session_all_return_204_and_family_ends_closed()
    {
        var (user, _) = await SeedActiveUserAsync();
        using var loginClient = CreateClient();
        var cookie = await LoginAndReturnCookieValueAsync(loginClient, user.Email!);

        var responses = await RunInParallelAsync(5, async _ =>
        {
            using var client = CreateClient();
            return await client.SendAsync(BuildLogoutRequest(cookie));
        });

        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            var tokens = await db.RefreshTokens.AsNoTracking()
                .Where(t => t.UserId == user.Id)
                .ToListAsync();
            Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
            Assert.All(tokens, t => Assert.Equal(RefreshTokenRevocationReason.Logout, t.ReasonRevoked));
        }
        finally
        {
            foreach (var r in responses)
            {
                r.Dispose();
            }
        }
    }

    [Fact]
    public async Task Logout_vs_refresh_race_leaves_no_active_family_tokens_and_no_500()
    {
        var (user, _) = await SeedActiveUserAsync();
        using var setupClient = CreateClient();
        var cookie = await LoginAndReturnCookieValueAsync(setupClient, user.Email!);

        var barrier = new Barrier(2);
        var refreshTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            using var client = CreateClient();
            return await client.SendAsync(BuildRefreshRequest(cookie));
        });
        var logoutTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            using var client = CreateClient();
            return await client.SendAsync(BuildLogoutRequest(cookie));
        });

        using var refreshResponse = await refreshTask;
        using var logoutResponse = await logoutTask;

        Assert.NotEqual(HttpStatusCode.InternalServerError, refreshResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, logoutResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        // Final DB invariant: the family has NO active tokens, regardless
        // of which request won.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id).ToListAsync();
        Assert.NotEmpty(tokens);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));

        // If refresh happened to win, prove the successor it just issued
        // is now unusable (subsequent refresh with it returns 401).
        if (refreshResponse.StatusCode == HttpStatusCode.OK)
        {
            var successor = ExtractCookieValue(refreshResponse);
            Assert.NotNull(successor);
            using var probeClient = CreateClient();
            using var probe = await probeClient.SendAsync(BuildRefreshRequest(successor));
            Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);
        }
    }

    [Fact]
    public async Task Different_user_logouts_are_isolated()
    {
        var (userA, _) = await SeedActiveUserAsync();
        var (userB, _) = await SeedActiveUserAsync();
        using var setupClient = CreateClient();
        var cookieA = await LoginAndReturnCookieValueAsync(setupClient, userA.Email!);
        var cookieB = await LoginAndReturnCookieValueAsync(setupClient, userB.Email!);

        var barrier = new Barrier(2);
        var taskA = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            using var client = CreateClient();
            return await client.SendAsync(BuildLogoutRequest(cookieA));
        });
        var taskB = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            using var client = CreateClient();
            return await client.SendAsync(BuildLogoutRequest(cookieB));
        });

        using var responseA = await taskA;
        using var responseB = await taskB;
        Assert.Equal(HttpStatusCode.NoContent, responseA.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, responseB.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var tokensA = await db.RefreshTokens.AsNoTracking().Where(t => t.UserId == userA.Id).ToListAsync();
        var tokensB = await db.RefreshTokens.AsNoTracking().Where(t => t.UserId == userB.Id).ToListAsync();
        Assert.All(tokensA, t => Assert.Equal(RefreshTokenRevocationReason.Logout, t.ReasonRevoked));
        Assert.All(tokensB, t => Assert.Equal(RefreshTokenRevocationReason.Logout, t.ReasonRevoked));
    }
}
