using System.Globalization;
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
using OpsFlow.Contracts.Authentication;
using OpsFlow.Domain.Organizations;
using OpsFlow.Infrastructure.Authentication;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Authentication;

[Collection(SqlServerCollection.Name)]
public sealed class LoginEndpointTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string LoginPath = "/api/v1/auth/login";

    private readonly OpsFlowWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public LoginEndpointTests(SqlServerFixture fixture)
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

    private async Task<(ApplicationUser User, Organization Org)> SeedActiveUserAsync(
        string password = DefaultPassword,
        string? role = null,
        bool userActive = true,
        bool orgActive = true,
        int preExistingFailedAttempts = 0)
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider, orgActive);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, password, isActive: userActive, role: role);

        if (preExistingFailedAttempts > 0)
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            for (var i = 0; i < preExistingFailedAttempts; i++)
            {
                await userManager.AccessFailedAsync(user);
            }
        }

        return (user, org);
    }

    private static HttpRequestMessage CreateLoginRequest(string email, string password)
    {
        var json = JsonSerializer.Serialize(new { email, password });
        return new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static string? ExtractRefreshTokenFromSetCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        const string prefix = "opsflow_refresh_token=";
        foreach (var cookie in cookies)
        {
            if (!cookie.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var valueEnd = cookie.IndexOf(';', prefix.Length);
            return valueEnd < 0
                ? cookie[prefix.Length..]
                : cookie[prefix.Length..valueEnd];
        }

        return null;
    }

    private static void AssertNoSetCookie(HttpResponseMessage response)
    {
        Assert.False(
            response.Headers.Contains("Set-Cookie"),
            "Response must not contain a Set-Cookie header.");
    }

    private static async Task AssertEmptyBody(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            string.IsNullOrEmpty(body),
            $"Response body must be empty but was: {body}");
    }

    // ================================================================
    // Happy path
    // ================================================================

    [Fact]
    public async Task Login_with_valid_credentials_returns_200_with_access_token_and_user()
    {
        var (user, org) = await SeedActiveUserAsync(role: "Admin");

        var response = await _client.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login);
        Assert.False(string.IsNullOrWhiteSpace(login.AccessToken));
        Assert.True(login.AccessTokenExpiresAt > DateTimeOffset.UtcNow);

        Assert.NotNull(login.User);
        Assert.Equal(user.Id, login.User.UserId);
        Assert.Equal(user.Email, login.User.Email);
        Assert.Equal(user.DisplayName, login.User.DisplayName);
        Assert.Equal(org.Id, login.User.OrganizationId);
        Assert.Equal(org.Name, login.User.OrganizationName);
        Assert.Contains("Admin", login.User.Roles);
    }

    [Fact]
    public async Task Login_with_valid_credentials_sets_refresh_cookie_with_correct_attributes()
    {
        var (user, _) = await SeedActiveUserAsync();

        var response = await _client.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(
            response.Headers.TryGetValues("Set-Cookie", out var cookies),
            "Response must contain a Set-Cookie header.");

        var cookieHeaders = cookies.ToList();
        var refreshCookie = cookieHeaders.FirstOrDefault(
            c => c.StartsWith("opsflow_refresh_token=", StringComparison.Ordinal));

        Assert.NotNull(refreshCookie);
        Assert.Contains("httponly", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", refreshCookie, StringComparison.OrdinalIgnoreCase);

        var otherAuthCookies = cookieHeaders
            .Where(c => !c.StartsWith("opsflow_refresh_token=", StringComparison.Ordinal))
            .Where(c => c.Contains("token", StringComparison.OrdinalIgnoreCase)
                        || c.Contains("auth", StringComparison.OrdinalIgnoreCase)
                        || c.Contains("session", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(otherAuthCookies);
    }

    [Fact]
    public async Task Login_with_valid_credentials_cookie_expiration_matches_persisted_token()
    {
        var (user, _) = await SeedActiveUserAsync();

        var response = await _client.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        var refreshCookie = cookies.First(
            c => c.StartsWith("opsflow_refresh_token=", StringComparison.Ordinal));

        var expiresSegment = refreshCookie.Split(';')
            .Select(s => s.Trim())
            .First(s => s.StartsWith("expires=", StringComparison.OrdinalIgnoreCase));
        var cookieExpires = DateTimeOffset.Parse(
            expiresSegment["expires=".Length..],
            CultureInfo.InvariantCulture);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var persistedToken = await db.RefreshTokens.FirstAsync(t => t.UserId == user.Id);

        Assert.True(
            Math.Abs((cookieExpires - persistedToken.ExpiresAt).TotalSeconds) <= 1.0,
            $"Cookie Expires {cookieExpires:O} differs from persisted ExpiresAt {persistedToken.ExpiresAt:O} by more than 1 second.");
    }

    [Fact]
    public async Task Login_with_valid_credentials_does_not_leak_secrets_in_json()
    {
        var (user, _) = await SeedActiveUserAsync();

        var response = await _client.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rawJson = await response.Content.ReadAsStringAsync();
        var rawRefreshToken = ExtractRefreshTokenFromSetCookie(response);
        Assert.NotNull(rawRefreshToken);

        Assert.DoesNotContain(rawRefreshToken, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("TokenHash", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecurityStamp", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_with_valid_credentials_persists_correct_refresh_token_hash()
    {
        var (user, _) = await SeedActiveUserAsync();

        var response = await _client.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rawRefreshToken = ExtractRefreshTokenFromSetCookie(response);
        Assert.NotNull(rawRefreshToken);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var tokens = await db.RefreshTokens
            .Where(t => t.UserId == user.Id)
            .ToListAsync();

        Assert.Single(tokens);
        var persisted = tokens[0];

        Assert.NotEqual(rawRefreshToken, persisted.TokenHash);

        var hasher = _factory.Services.GetRequiredService<IRefreshTokenHasher>();
        Assert.Equal(hasher.Hash(rawRefreshToken), persisted.TokenHash);
    }

    [Fact]
    public async Task Login_with_valid_credentials_resets_access_failed_count_and_updates_last_login()
    {
        var (user, _) = await SeedActiveUserAsync(preExistingFailedAttempts: 2);

        var response = await _client.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var updatedUser = await db.Users.FirstAsync(u => u.Id == user.Id);

        Assert.Equal(0, updatedUser.AccessFailedCount);
        Assert.NotNull(updatedUser.LastLoginAt);
        Assert.True(updatedUser.LastLoginAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    // ================================================================
    // Failure path — uniform 401
    // ================================================================

    [Fact]
    public async Task Login_with_wrong_password_returns_401_without_body_or_cookie()
    {
        var (user, _) = await SeedActiveUserAsync();

        var response = await _client.SendAsync(CreateLoginRequest(user.Email!, "WrongP@ssw0rd1"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertEmptyBody(response);
        AssertNoSetCookie(response);
    }

    [Fact]
    public async Task Login_with_nonexistent_email_returns_401_without_body_or_cookie()
    {
        var response = await _client.SendAsync(
            CreateLoginRequest(
                "nonexistent-" + Guid.NewGuid().ToString("N") + "@test.local",
                DefaultPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertEmptyBody(response);
        AssertNoSetCookie(response);
    }

    [Fact]
    public async Task Login_with_locked_user_returns_401_without_body_or_cookie()
    {
        var (user, _) = await SeedActiveUserAsync(preExistingFailedAttempts: 5);

        var response = await _client.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertEmptyBody(response);
        AssertNoSetCookie(response);
    }

    [Fact]
    public async Task Login_with_inactive_user_returns_401_without_body_or_cookie()
    {
        var (user, _) = await SeedActiveUserAsync(userActive: false);

        var response = await _client.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertEmptyBody(response);
        AssertNoSetCookie(response);
    }

    [Fact]
    public async Task Login_with_inactive_organization_returns_401_without_body_or_cookie()
    {
        var (user, _) = await SeedActiveUserAsync(orgActive: false);

        var response = await _client.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertEmptyBody(response);
        AssertNoSetCookie(response);
    }

    // ================================================================
    // Validation — 400
    // ================================================================

    [Fact]
    public async Task Login_with_null_body_returns_400()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json"),
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNoSetCookie(response);
    }

    [Fact]
    public async Task Login_with_empty_email_returns_400()
    {
        var response = await _client.SendAsync(CreateLoginRequest("", DefaultPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNoSetCookie(response);
    }

    [Fact]
    public async Task Login_with_empty_password_returns_400()
    {
        var response = await _client.SendAsync(CreateLoginRequest("test@test.local", ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNoSetCookie(response);
    }

    // ================================================================
    // Server error — 500
    // ================================================================

    [Fact]
    public async Task Login_server_error_does_not_return_401_and_does_not_emit_cookie()
    {
        var (user, _) = await SeedActiveUserAsync();

        using var errorFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILoginSessionIssuer>();
                services.AddScoped<ILoginSessionIssuer, ThrowingLoginSessionIssuer>();
            });
        });
        var errorClient = errorFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        var response = await errorClient.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertNoSetCookie(response);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("refresh", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecurityStamp", body, StringComparison.OrdinalIgnoreCase);
    }
}
