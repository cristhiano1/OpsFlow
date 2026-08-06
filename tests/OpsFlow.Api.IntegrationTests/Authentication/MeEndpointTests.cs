using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Application.Authorization;
using OpsFlow.Contracts.Authentication;
using OpsFlow.Domain.Organizations;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Authentication;

[Collection(SqlServerCollection.Name)]
public sealed class MeEndpointTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string LoginPath = "/api/v1/auth/login";
    private const string MePath = "/api/v1/auth/me";
    private const string CookieName = "opsflow_refresh_token";

    private readonly OpsFlowWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MeEndpointTests(SqlServerFixture fixture)
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
        string? role = OpsFlowRoles.Coordinator,
        bool userActive = true,
        bool orgActive = true)
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider, orgActive);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, password, isActive: userActive, role: role);
        return (user, org);
    }

    private async Task<LoginSession> LoginAsync(string email, string password = DefaultPassword)
    {
        var json = JsonSerializer.Serialize(new { email, password });
        using var msg = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);
        return new LoginSession(body.AccessToken, ExtractRefreshCookieValue(response));
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

    private static HttpRequestMessage BuildMeRequest(string? bearerToken, string? refreshCookie = null)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, MePath);
        if (bearerToken is not null)
        {
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        if (refreshCookie is not null)
        {
            msg.Headers.Add("Cookie", $"{CookieName}={refreshCookie}");
        }
        return msg;
    }

    private string MintTokenWithCustomClaims(IEnumerable<Claim> claims, TimeSpan? lifetime = null)
    {
        var jwtOptions = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var signingKey = new SymmetricSecurityKey(Convert.FromBase64String(jwtOptions.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var now = DateTimeOffset.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwtOptions.Issuer,
            Audience = jwtOptions.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(lifetime ?? TimeSpan.FromMinutes(15)).UtcDateTime,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = credentials,
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    // ================================================================
    // Success paths
    // ================================================================

    [Fact]
    public async Task Me_with_valid_bearer_returns_current_profile()
    {
        var (user, org) = await SeedActiveUserAsync();
        var session = await LoginAsync(user.Email!);

        using var response = await _client.SendAsync(BuildMeRequest(session.AccessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginUserResponse>();
        Assert.NotNull(body);
        Assert.Equal(user.Id, body!.UserId);
        Assert.Equal(user.Email, body.Email);
        Assert.Equal(user.DisplayName, body.DisplayName);
        Assert.Equal(org.Id, body.OrganizationId);
        Assert.Equal(org.Name, body.OrganizationName);
        Assert.Contains(OpsFlowRoles.Coordinator, body.Roles);
    }

    [Fact]
    public async Task Me_returns_current_DB_profile_when_display_name_changes_after_issuance()
    {
        var (user, _) = await SeedActiveUserAsync();
        var session = await LoginAsync(user.Email!);

        const string updatedDisplayName = "Renamed User";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            var tracked = await db.Users.SingleAsync(u => u.Id == user.Id);
            tracked.DisplayName = updatedDisplayName;
            _ = await db.SaveChangesAsync();
        }

        using var response = await _client.SendAsync(BuildMeRequest(session.AccessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginUserResponse>();
        Assert.NotNull(body);
        Assert.Equal(updatedDisplayName, body!.DisplayName);
    }

    [Fact]
    public async Task Me_reflects_role_added_after_issuance()
    {
        var (user, _) = await SeedActiveUserAsync(role: OpsFlowRoles.Viewer);
        var session = await LoginAsync(user.Email!);

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roleManager.RoleExistsAsync(OpsFlowRoles.Technician))
            {
                _ = await roleManager.CreateAsync(new IdentityRole<Guid>(OpsFlowRoles.Technician)
                {
                    Id = Guid.NewGuid(),
                });
            }
            var tracked = await userManager.FindByIdAsync(user.Id.ToString());
            _ = await userManager.AddToRoleAsync(tracked!, OpsFlowRoles.Technician);
        }

        using var response = await _client.SendAsync(BuildMeRequest(session.AccessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginUserResponse>();
        Assert.NotNull(body);
        Assert.Contains(OpsFlowRoles.Viewer, body!.Roles);
        Assert.Contains(OpsFlowRoles.Technician, body.Roles);
    }

    [Fact]
    public async Task Me_successful_response_is_non_storable()
    {
        var (user, _) = await SeedActiveUserAsync();
        var session = await LoginAsync(user.Email!);

        using var response = await _client.SendAsync(BuildMeRequest(session.AccessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The /me response reflects authoritative DB state and must never be
        // reused from an HTTP cache. `no-store` is the load-bearing directive;
        // any additional directives emitted by ResponseCache are accepted.
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(
            response.Headers.CacheControl!.NoStore,
            "GET /me must set Cache-Control: no-store so intermediaries and browsers cannot serve a cached copy.");
    }

    [Fact]
    public async Task Me_does_not_mutate_refresh_cookie()
    {
        var (user, _) = await SeedActiveUserAsync();
        var session = await LoginAsync(user.Email!);

        using var response = await _client.SendAsync(
            BuildMeRequest(session.AccessToken, session.RefreshCookieValue));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(
            response.Headers.Contains("Set-Cookie"),
            "GET /me must never emit any Set-Cookie header.");
    }

    [Fact]
    public async Task Me_response_body_does_not_leak_sensitive_fields()
    {
        var (user, _) = await SeedActiveUserAsync();
        var session = await LoginAsync(user.Email!);

        using var response = await _client.SendAsync(BuildMeRequest(session.AccessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SecurityStamp", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sstamp", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConcurrencyStamp", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PasswordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LockoutEnd", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TokenHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RefreshToken", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    // Bearer-level failures (JwtBearer middleware rejects)
    // ================================================================

    [Fact]
    public async Task Me_without_bearer_returns_401()
    {
        using var response = await _client.SendAsync(BuildMeRequest(null));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_with_malformed_bearer_returns_401()
    {
        using var response = await _client.SendAsync(BuildMeRequest("not-a-jwt"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_with_wrong_signature_returns_401()
    {
        // Sign with a random key that the API does NOT know about.
        var foreignKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        var credentials = new SigningCredentials(foreignKey, SecurityAlgorithms.HmacSha256);
        var jwtOptions = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

        var claims = new[]
        {
            new Claim(OpsFlowClaimTypes.Subject, Guid.NewGuid().ToString()),
            new Claim(OpsFlowClaimTypes.SecurityStamp, "any"),
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwtOptions.Issuer,
            Audience = jwtOptions.Audience,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = credentials,
        };
        var token = new JsonWebTokenHandler().CreateToken(descriptor);

        using var response = await _client.SendAsync(BuildMeRequest(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_with_expired_bearer_returns_401()
    {
        var (user, _) = await SeedActiveUserAsync();
        // Reload to capture the seeded SecurityStamp so the token is realistic.
        string stamp;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            stamp = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).SecurityStamp!;
        }

        var claims = new[]
        {
            new Claim(OpsFlowClaimTypes.Subject, user.Id.ToString()),
            new Claim(OpsFlowClaimTypes.SecurityStamp, stamp),
        };
        var token = MintTokenWithCustomClaims(claims, lifetime: TimeSpan.FromMinutes(-10));

        using var response = await _client.SendAsync(BuildMeRequest(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ================================================================
    // Claim-shape failures inside the controller
    // ================================================================

    [Fact]
    public async Task Me_with_valid_signature_but_missing_sub_returns_401()
    {
        var claims = new[]
        {
            new Claim(OpsFlowClaimTypes.SecurityStamp, "any-stamp-value"),
        };
        var token = MintTokenWithCustomClaims(claims);

        using var response = await _client.SendAsync(BuildMeRequest(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_with_valid_signature_but_non_guid_sub_returns_401()
    {
        var claims = new[]
        {
            new Claim(OpsFlowClaimTypes.Subject, "not-a-guid"),
            new Claim(OpsFlowClaimTypes.SecurityStamp, "any-stamp-value"),
        };
        var token = MintTokenWithCustomClaims(claims);

        using var response = await _client.SendAsync(BuildMeRequest(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_with_valid_signature_but_empty_guid_sub_returns_401()
    {
        var claims = new[]
        {
            new Claim(OpsFlowClaimTypes.Subject, Guid.Empty.ToString()),
            new Claim(OpsFlowClaimTypes.SecurityStamp, "any-stamp-value"),
        };
        var token = MintTokenWithCustomClaims(claims);

        using var response = await _client.SendAsync(BuildMeRequest(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_with_valid_signature_but_missing_sstamp_returns_401()
    {
        var claims = new[]
        {
            new Claim(OpsFlowClaimTypes.Subject, Guid.NewGuid().ToString()),
        };
        var token = MintTokenWithCustomClaims(claims);

        using var response = await _client.SendAsync(BuildMeRequest(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_with_valid_signature_but_blank_sstamp_returns_401()
    {
        var claims = new[]
        {
            new Claim(OpsFlowClaimTypes.Subject, Guid.NewGuid().ToString()),
            new Claim(OpsFlowClaimTypes.SecurityStamp, "   "),
        };
        var token = MintTokenWithCustomClaims(claims);

        using var response = await _client.SendAsync(BuildMeRequest(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ================================================================
    // Authoritative-DB failures
    // ================================================================

    [Fact]
    public async Task Me_after_user_deletion_returns_401()
    {
        var (user, _) = await SeedActiveUserAsync();
        var session = await LoginAsync(user.Email!);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            _ = await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM dbo.AspNetUserRoles WHERE UserId = {0}; " +
                "DELETE FROM dbo.RefreshTokens WHERE UserId = {0}; " +
                "DELETE FROM dbo.AspNetUsers WHERE Id = {0};",
                user.Id);
        }

        using var response = await _client.SendAsync(BuildMeRequest(session.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_after_user_deactivation_returns_401()
    {
        var (user, _) = await SeedActiveUserAsync();
        var session = await LoginAsync(user.Email!);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            var tracked = await db.Users.SingleAsync(u => u.Id == user.Id);
            tracked.IsActive = false;
            _ = await db.SaveChangesAsync();
        }

        using var response = await _client.SendAsync(BuildMeRequest(session.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_after_user_lockout_returns_401()
    {
        var (user, _) = await SeedActiveUserAsync();
        var session = await LoginAsync(user.Email!);

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var tracked = await userManager.FindByIdAsync(user.Id.ToString());
            _ = await userManager.SetLockoutEndDateAsync(
                tracked!, DateTimeOffset.UtcNow.AddHours(1));
        }

        using var response = await _client.SendAsync(BuildMeRequest(session.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_after_org_deactivation_returns_401()
    {
        var (user, org) = await SeedActiveUserAsync();
        var session = await LoginAsync(user.Email!);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            var tracked = await db.Organizations.SingleAsync(o => o.Id == org.Id);
            tracked.IsActive = false;
            _ = await db.SaveChangesAsync();
        }

        using var response = await _client.SendAsync(BuildMeRequest(session.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_when_organization_row_is_missing_returns_401()
    {
        var (user, _) = await SeedActiveUserAsync();
        var session = await LoginAsync(user.Email!);

        // Bypass the User→Organization FK long enough to point the user at a
        // non-existent organization id. Cleanup below removes the orphan and
        // then re-enables the FK WITH CHECK so the shared SqlServerFixture is
        // left in the same enabled + trusted state migrations produce.
        var orphanOrgId = Guid.NewGuid();
        using var setupScope = _factory.Services.CreateScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();

        _ = await setupDb.Database.ExecuteSqlRawAsync(
            "ALTER TABLE dbo.AspNetUsers NOCHECK CONSTRAINT [FK_AspNetUsers_Organizations_OrganizationId];");
        try
        {
            _ = await setupDb.Database.ExecuteSqlRawAsync(
                "UPDATE dbo.AspNetUsers SET OrganizationId = {0} WHERE Id = {1};",
                orphanOrgId, user.Id);

            using var response = await _client.SendAsync(BuildMeRequest(session.AccessToken));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            // Load-bearing ordering: the orphan row must be removed BEFORE
            // the constraint is re-enabled WITH CHECK, otherwise the trust
            // re-validation would fail on the surviving row and leave the FK
            // untrusted. The nested try/finally guarantees the re-trust step
            // always runs, and neither exception is swallowed — the later
            // exception simply replaces the earlier one per C# semantics.
            try
            {
                _ = await setupDb.Database.ExecuteSqlRawAsync(
                    "DELETE FROM dbo.AspNetUserRoles WHERE UserId = {0}; " +
                    "DELETE FROM dbo.RefreshTokens WHERE UserId = {0}; " +
                    "DELETE FROM dbo.AspNetUsers WHERE Id = {0};",
                    user.Id);
            }
            finally
            {
                _ = await setupDb.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE dbo.AspNetUsers WITH CHECK CHECK CONSTRAINT [FK_AspNetUsers_Organizations_OrganizationId];");
            }
        }

        using var probeScope = _factory.Services.CreateScope();
        var probeDb = probeScope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        await UserOrgForeignKeyProbe.AssertEnabledAndTrustedAsync(probeDb);
    }

    [Fact]
    public async Task Me_after_security_stamp_rotation_returns_401()
    {
        var (user, _) = await SeedActiveUserAsync();
        var session = await LoginAsync(user.Email!);

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var tracked = await userManager.FindByIdAsync(user.Id.ToString());
            _ = await userManager.UpdateSecurityStampAsync(tracked!);
        }

        using var response = await _client.SendAsync(BuildMeRequest(session.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record LoginSession(string AccessToken, string? RefreshCookieValue);
}
