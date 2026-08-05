using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Application.Authentication;
using OpsFlow.Application.Authorization;
using OpsFlow.Contracts.Authentication;
using OpsFlow.Domain.Organizations;
using OpsFlow.Infrastructure.Authentication;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Authentication;

[Collection(SqlServerCollection.Name)]
public sealed class RefreshEndpointTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string OtherPassword = "OtherStr0ng!Pw22";
    private const string LoginPath = "/api/v1/auth/login";
    private const string RefreshPath = "/api/v1/auth/refresh";
    private const string CookieName = "opsflow_refresh_token";

    private readonly OpsFlowWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RefreshEndpointTests(SqlServerFixture fixture)
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

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private async Task<(ApplicationUser User, Organization Org)> SeedActiveUserAsync(
        string password = DefaultPassword,
        string? role = null,
        bool userActive = true,
        bool orgActive = true)
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider, orgActive);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, password, isActive: userActive, role: role);
        return (user, org);
    }

    private async Task<string> LoginAndReturnCookieValueAsync(string email, string password)
    {
        var json = JsonSerializer.Serialize(new { email, password });
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await _client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = ExtractRefreshCookieValue(response);
        Assert.NotNull(raw);
        return raw;
    }

    private static HttpRequestMessage BuildRefreshRequest(string? cookieValue)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, RefreshPath);
        if (cookieValue is not null)
        {
            msg.Headers.Add("Cookie", $"{CookieName}={cookieValue}");
        }
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

    private static void AssertNoSetCookie(HttpResponseMessage response) =>
        Assert.False(
            response.Headers.Contains("Set-Cookie"),
            "Response must not contain a Set-Cookie header.");

    // ================================================================
    // Success paths
    // ================================================================

    [Fact]
    public async Task Valid_refresh_returns_200_with_access_token_and_user()
    {
        var (user, org) = await SeedActiveUserAsync(role: OpsFlowRoles.Viewer);
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!, DefaultPassword);

        using var response = await _client.SendAsync(BuildRefreshRequest(loginCookie));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RefreshResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body.AccessToken));
        Assert.True(body.AccessTokenExpiresAt > DateTimeOffset.UtcNow);
        Assert.Equal(user.Id, body.User.UserId);
        Assert.Equal(org.Name, body.User.OrganizationName);
        Assert.Contains(OpsFlowRoles.Viewer, body.User.Roles);
    }

    [Fact]
    public async Task Valid_refresh_sets_new_refresh_cookie_with_correct_attributes()
    {
        var (user, _) = await SeedActiveUserAsync();
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!, DefaultPassword);

        using var response = await _client.SendAsync(BuildRefreshRequest(loginCookie));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        var refreshCookie = cookies.First(c => c.StartsWith(CookieName + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", refreshCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Valid_refresh_persists_new_hashed_token_and_never_the_raw_value()
    {
        var (user, _) = await SeedActiveUserAsync();
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!, DefaultPassword);

        using var response = await _client.SendAsync(BuildRefreshRequest(loginCookie));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var successorRaw = ExtractRefreshCookieValue(response);
        Assert.NotNull(successorRaw);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id)
            .ToListAsync();
        Assert.Equal(2, tokens.Count);
        var hasher = scope.ServiceProvider.GetRequiredService<IRefreshTokenHasher>();
        var successorHash = hasher.Hash(successorRaw!);
        Assert.Contains(tokens, t => t.TokenHash == successorHash && t.RevokedAt == null);
        Assert.All(tokens, t => Assert.NotEqual(successorRaw, t.TokenHash));
    }

    [Fact]
    public async Task Valid_refresh_marks_old_token_as_rotated_and_makes_it_unusable()
    {
        var (user, _) = await SeedActiveUserAsync();
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!, DefaultPassword);

        using (var response = await _client.SendAsync(BuildRefreshRequest(loginCookie)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Replay the ORIGINAL cookie value — must fail.
        using var replay = await _client.SendAsync(BuildRefreshRequest(loginCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        AssertNoSetCookie(replay);

        // After the replay, family should be revoked with ReuseDetected.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id).ToListAsync();
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
        Assert.Contains(tokens,
            t => t.ReasonRevoked == RefreshTokenRevocationReason.ReuseDetected);
    }

    [Fact]
    public async Task Refresh_reflects_current_roles_when_role_is_added_after_login()
    {
        var (user, _) = await SeedActiveUserAsync(role: OpsFlowRoles.Viewer);
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!, DefaultPassword);

        using (var scope = _factory.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roles.RoleExistsAsync(OpsFlowRoles.Coordinator))
            {
                _ = await roles.CreateAsync(
                    new IdentityRole<Guid>(OpsFlowRoles.Coordinator) { Id = Guid.NewGuid() });
            }
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var tracked = await userManager.FindByIdAsync(user.Id.ToString());
            Assert.NotNull(tracked);
            _ = await userManager.AddToRoleAsync(tracked!, OpsFlowRoles.Coordinator);
        }

        using var response = await _client.SendAsync(BuildRefreshRequest(loginCookie));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RefreshResponse>();
        Assert.NotNull(body);
        Assert.Contains(OpsFlowRoles.Coordinator, body.User.Roles);
        Assert.Contains(OpsFlowRoles.Viewer, body.User.Roles);
    }

    // ================================================================
    // Uniform 401 failure paths
    // ================================================================

    [Fact]
    public async Task Missing_cookie_returns_401_with_empty_body_and_no_cookie()
    {
        using var response = await _client.SendAsync(BuildRefreshRequest(null));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(await response.Content.ReadAsStringAsync()));
        AssertNoSetCookie(response);
    }

    [Fact]
    public async Task Malformed_cookie_returns_401_with_empty_body_and_no_cookie()
    {
        using var response = await _client.SendAsync(
            BuildRefreshRequest("garbage-value-does-not-match-any-hash"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(await response.Content.ReadAsStringAsync()));
        AssertNoSetCookie(response);
    }

    [Fact]
    public async Task Inactive_user_returns_401_with_no_cookie()
    {
        var (user, _) = await SeedActiveUserAsync();
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!, DefaultPassword);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            var u = await db.Users.SingleAsync(x => x.Id == user.Id);
            u.IsActive = false;
            await db.SaveChangesAsync();
        }

        using var response = await _client.SendAsync(BuildRefreshRequest(loginCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNoSetCookie(response);
    }

    [Fact]
    public async Task Inactive_organization_returns_401_with_no_cookie()
    {
        var (user, _) = await SeedActiveUserAsync();
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!, DefaultPassword);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            var u = await db.Users.SingleAsync(x => x.Id == user.Id);
            var o = await db.Organizations.SingleAsync(x => x.Id == u.OrganizationId);
            o.IsActive = false;
            await db.SaveChangesAsync();
        }

        using var response = await _client.SendAsync(BuildRefreshRequest(loginCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNoSetCookie(response);
    }

    [Fact]
    public async Task Password_change_after_login_causes_refresh_401_with_no_cookie()
    {
        var (user, _) = await SeedActiveUserAsync();
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!, DefaultPassword);

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var tracked = await userManager.FindByIdAsync(user.Id.ToString());
            Assert.NotNull(tracked);
            var change = await userManager.ChangePasswordAsync(tracked!, DefaultPassword, OtherPassword);
            Assert.True(change.Succeeded);
        }

        using var response = await _client.SendAsync(BuildRefreshRequest(loginCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNoSetCookie(response);
    }

    [Fact]
    public async Task Response_body_does_not_leak_refresh_token_or_secrets()
    {
        var (user, _) = await SeedActiveUserAsync();
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!, DefaultPassword);

        using var response = await _client.SendAsync(BuildRefreshRequest(loginCookie));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rawJson = await response.Content.ReadAsStringAsync();
        var successorRaw = ExtractRefreshCookieValue(response);
        Assert.NotNull(successorRaw);
        Assert.DoesNotContain(successorRaw!, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("TokenHash", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecurityStamp", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unexpected_infrastructure_failure_returns_500_without_cookie()
    {
        var (user, _) = await SeedActiveUserAsync();
        var loginCookie = await LoginAndReturnCookieValueAsync(user.Email!, DefaultPassword);

        using var errorFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRefreshSessionRotator>();
                services.AddScoped<IRefreshSessionRotator, ThrowingRefreshSessionRotator>();
            });
        });
        using var errorClient = errorFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        using var response = await errorClient.SendAsync(BuildRefreshRequest(loginCookie));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertNoSetCookie(response);
    }
}

internal sealed class ThrowingRefreshSessionRotator : IRefreshSessionRotator
{
    public Task<RefreshRotationResult> RotateAsync(
        RefreshRotationRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated infrastructure failure.");
}
