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
/// Deterministic concurrency coverage for POST /api/v1/auth/refresh using
/// xUnit Barriers. No Thread.Sleep, no wall-clock race coordination, no
/// elapsed-time correctness assertions.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RefreshConcurrencyTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string LoginPath = "/api/v1/auth/login";
    private const string RefreshPath = "/api/v1/auth/refresh";
    private const string CookieName = "opsflow_refresh_token";

    private readonly OpsFlowWebApplicationFactory _factory;

    public RefreshConcurrencyTests(SqlServerFixture fixture)
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
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, DefaultPassword);
        return (user, org);
    }

    private static async Task<string> LoginAndReturnCookieValueAsync(HttpClient client, string email)
    {
        var json = JsonSerializer.Serialize(new { email, password = DefaultPassword });
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ExtractRefreshCookieValue(response)!;
    }

    private static HttpRequestMessage BuildRefreshRequest(string cookieValue)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, RefreshPath);
        msg.Headers.Add("Cookie", $"{CookieName}={cookieValue}");
        return msg;
    }

    private static string? ExtractRefreshCookieValue(HttpResponseMessage response)
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
    public async Task Concurrent_refresh_of_same_token_yields_exactly_one_success_others_401_no_family_revocation()
    {
        var (user, _) = await SeedActiveUserAsync();
        using var loginClient = CreateClient();
        var loginCookie = await LoginAndReturnCookieValueAsync(loginClient, user.Email!);

        const int parallelCount = 5;
        var responses = await RunInParallelAsync(parallelCount, async _ =>
        {
            using var client = CreateClient();
            return await client.SendAsync(BuildRefreshRequest(loginCookie));
        });

        try
        {
            var successCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
            var unauthorizedCount = responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized);
            Assert.Equal(1, successCount);
            Assert.Equal(parallelCount - 1, unauthorizedCount);

            // Persist checks: exactly one successor row, and old token was
            // revoked as Rotated (concurrent-race losers must NOT have
            // triggered a ReuseDetected family revocation).
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            var tokens = await db.RefreshTokens.AsNoTracking()
                .Where(t => t.UserId == user.Id)
                .ToListAsync();
            Assert.Equal(2, tokens.Count);
            Assert.Contains(tokens, t => t.RevokedAt is not null
                && t.ReasonRevoked == RefreshTokenRevocationReason.Rotated);
            Assert.Contains(tokens, t => t.RevokedAt is null);
            Assert.DoesNotContain(tokens,
                t => t.ReasonRevoked == RefreshTokenRevocationReason.ReuseDetected);
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
    public async Task Delayed_replay_after_successful_rotation_triggers_family_revocation()
    {
        var (user, _) = await SeedActiveUserAsync();
        using var loginClient = CreateClient();
        var loginCookie = await LoginAndReturnCookieValueAsync(loginClient, user.Email!);

        // Legitimate refresh completes first.
        using (var okClient = CreateClient())
        using (var okResponse = await okClient.SendAsync(BuildRefreshRequest(loginCookie)))
        {
            Assert.Equal(HttpStatusCode.OK, okResponse.StatusCode);
        }

        // Now replay the ORIGINAL cookie value — clearly post-commit.
        using (var replayClient = CreateClient())
        using (var replayResponse = await replayClient.SendAsync(BuildRefreshRequest(loginCookie)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id)
            .ToListAsync();
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
        Assert.Contains(tokens,
            t => t.ReasonRevoked == RefreshTokenRevocationReason.ReuseDetected);
    }

    [Fact]
    public async Task Login_and_refresh_for_same_user_run_concurrently_without_deadlock()
    {
        var (user, _) = await SeedActiveUserAsync();
        using var setupClient = CreateClient();
        var loginCookie = await LoginAndReturnCookieValueAsync(setupClient, user.Email!);

        var barrier = new Barrier(2);
        var loginTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            using var client = CreateClient();
            var json = JsonSerializer.Serialize(new { email = user.Email, password = DefaultPassword });
            using var msg = new HttpRequestMessage(HttpMethod.Post, LoginPath)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return await client.SendAsync(msg);
        });
        var refreshTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            using var client = CreateClient();
            return await client.SendAsync(BuildRefreshRequest(loginCookie));
        });

        using var loginResponse = await loginTask;
        using var refreshResponse = await refreshTask;

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
    }
}
