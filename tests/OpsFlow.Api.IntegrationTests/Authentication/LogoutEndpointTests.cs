using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Application.Authentication;
using OpsFlow.Domain.Organizations;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Authentication;

[Collection(SqlServerCollection.Name)]
public sealed class LogoutEndpointTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string LoginPath = "/api/v1/auth/login";
    private const string LogoutPath = "/api/v1/auth/logout";
    private const string RefreshPath = "/api/v1/auth/refresh";
    private const string CookieName = "opsflow_refresh_token";

    private readonly OpsFlowWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public LogoutEndpointTests(SqlServerFixture fixture)
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

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<(ApplicationUser User, Organization Org)> SeedActiveUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(scope.ServiceProvider, org.Id, DefaultPassword);
        return (user, org);
    }

    private async Task<string> LoginAndReturnCookieValueAsync(string email)
    {
        var json = JsonSerializer.Serialize(new { email, password = DefaultPassword });
        using var msg = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = ExtractSetCookieValue(response);
        Assert.NotNull(value);
        return value;
    }

    private static HttpRequestMessage BuildLogoutRequest(string? cookieValue)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, LogoutPath);
        if (cookieValue is not null)
        {
            msg.Headers.Add("Cookie", $"{CookieName}={cookieValue}");
        }
        return msg;
    }

    private static HttpRequestMessage BuildRefreshRequest(string cookieValue)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, RefreshPath);
        msg.Headers.Add("Cookie", $"{CookieName}={cookieValue}");
        return msg;
    }

    private static string? ExtractSetCookieValue(HttpResponseMessage response)
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

    private static string? ExtractSetCookieHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }
        return cookies.FirstOrDefault(c => c.StartsWith(CookieName + "=", StringComparison.Ordinal));
    }

    private static void AssertDeletionCookieShape(string setCookieHeader)
    {
        // Deletion cookies are emitted as an empty value with Expires set to
        // the past. The security attributes must match issuance.
        Assert.StartsWith(CookieName + "=", setCookieHeader, StringComparison.Ordinal);
        Assert.Contains("path=/api/v1/auth", setCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", setCookieHeader, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    // Success paths
    // ================================================================

    [Fact]
    public async Task Active_logout_returns_204_deletes_cookie_and_revokes_family()
    {
        var (user, _) = await SeedActiveUserAsync();
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!);

        using var response = await _client.SendAsync(BuildLogoutRequest(loginCookie));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(await response.Content.ReadAsStringAsync()));

        var deletion = ExtractSetCookieHeader(response);
        Assert.NotNull(deletion);
        AssertDeletionCookieShape(deletion);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id).ToListAsync();
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
        Assert.All(tokens, t => Assert.Equal(RefreshTokenRevocationReason.Logout, t.ReasonRevoked));
    }

    [Fact]
    public async Task Missing_cookie_returns_204_with_defensive_deletion_and_no_db_change()
    {
        var (user, _) = await SeedActiveUserAsync();
        _ = await LoginAndReturnCookieValueAsync(user.Email!);

        int before;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            before = await db.RefreshTokens.AsNoTracking().CountAsync(t => t.RevokedAt != null);
        }

        using var response = await _client.SendAsync(BuildLogoutRequest(null));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertDeletionCookieShape(ExtractSetCookieHeader(response)!);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var after = await vdb.RefreshTokens.AsNoTracking().CountAsync(t => t.RevokedAt != null);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Unknown_cookie_returns_204_with_defensive_deletion_and_no_db_change()
    {
        var (user, _) = await SeedActiveUserAsync();
        _ = await LoginAndReturnCookieValueAsync(user.Email!);

        int before;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            before = await db.RefreshTokens.AsNoTracking().CountAsync(t => t.RevokedAt != null);
        }

        using var response = await _client.SendAsync(BuildLogoutRequest("garbage-value"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertDeletionCookieShape(ExtractSetCookieHeader(response)!);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var after = await vdb.RefreshTokens.AsNoTracking().CountAsync(t => t.RevokedAt != null);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Repeated_logout_returns_204_and_leaves_persisted_state_unchanged()
    {
        var (user, _) = await SeedActiveUserAsync();
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!);

        using (var first = await _client.SendAsync(BuildLogoutRequest(loginCookie)))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        RefreshToken afterFirst;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            afterFirst = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.UserId == user.Id);
        }

        using (var second = await _client.SendAsync(BuildLogoutRequest(loginCookie)))
        {
            Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        }

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var afterSecond = await vdb.RefreshTokens.AsNoTracking().SingleAsync(t => t.UserId == user.Id);
        Assert.Equal(afterFirst.RevokedAt, afterSecond.RevokedAt);
        Assert.Equal(afterFirst.ReasonRevoked, afterSecond.ReasonRevoked);
    }

    [Fact]
    public async Task Logout_response_body_does_not_leak_refresh_token_or_secrets()
    {
        var (user, _) = await SeedActiveUserAsync();
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!);

        using var response = await _client.SendAsync(BuildLogoutRequest(loginCookie));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(string.IsNullOrEmpty(raw));
        Assert.DoesNotContain(loginCookie, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("TokenHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecurityStamp", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Subsequent_refresh_using_logged_out_cookie_returns_401()
    {
        var (user, _) = await SeedActiveUserAsync();
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!);

        using (var logout = await _client.SendAsync(BuildLogoutRequest(loginCookie)))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        using var refresh = await _client.SendAsync(BuildRefreshRequest(loginCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        _ = user;
    }

    // ================================================================
    // Server failure path
    // ================================================================

    [Fact]
    public async Task Unexpected_infrastructure_failure_returns_500_without_cookie_deletion()
    {
        var (user, _) = await SeedActiveUserAsync();
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!);

        using var errorFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILogoutSessionRevoker>();
                services.AddScoped<ILogoutSessionRevoker, ThrowingLogoutSessionRevoker>();
            });
        });
        using var errorClient = errorFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        using var response = await errorClient.SendAsync(BuildLogoutRequest(loginCookie));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(
            response.Headers.Contains("Set-Cookie"),
            "500 responses must not emit a deletion Set-Cookie header.");
    }
}
