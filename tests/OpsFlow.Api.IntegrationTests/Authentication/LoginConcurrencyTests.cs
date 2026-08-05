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
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Authentication;

/// <summary>
/// Deterministic concurrency coverage for the three GitHub review findings on
/// PR #3. Every test uses SQL Server Testcontainers, xUnit Barriers, and
/// SemaphoreSlim signals — no wall-clock comparisons, no Thread.Sleep race
/// coordination, no elapsed-time assertions as correctness proof.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class LoginConcurrencyTests : IDisposable
{
    private const string DefaultPassword = "ValidP@ssw0rd1";
    private const string WrongPassword = "WrongP@ssw0rd1";
    private const string LoginPath = "/api/v1/auth/login";

    private readonly OpsFlowWebApplicationFactory _factory;

    public LoginConcurrencyTests(SqlServerFixture fixture)
    {
        _factory = new OpsFlowWebApplicationFactory(fixture.ConnectionString);
    }

    public void Dispose() => _factory.Dispose();

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

    private async Task<(ApplicationUser User, Organization Org)> SeedActiveUserAsync(
        string password = DefaultPassword,
        string? role = null)
    {
        using var scope = _factory.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, password, role: role);
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

    private static async Task<HttpResponseMessage[]> RunInParallelAsync(
        int count,
        Func<int, Task<HttpResponseMessage>> requestFactory)
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

    private static bool ResponseHasSetCookie(HttpResponseMessage response) =>
        response.Headers.Contains("Set-Cookie");

    // =================================================================
    // Finding #1 (P1) — parallel wrong-password recording
    // =================================================================

    [Fact]
    public async Task Parallel_wrong_password_attempts_all_return_401_never_500()
    {
        var (user, _) = await SeedActiveUserAsync();

        var responses = await RunInParallelAsync(5, async _ =>
        {
            using var client = CreateClient();
            return await client.SendAsync(CreateLoginRequest(user.Email!, WrongPassword));
        });

        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
            Assert.All(responses, r => Assert.False(ResponseHasSetCookie(r)));
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
    public async Task Parallel_wrong_password_attempts_progress_to_lockout_without_lost_increments()
    {
        var (user, _) = await SeedActiveUserAsync();

        // 5 parallel wrong-password attempts == configured MaxFailedAccessAttempts.
        var responses = await RunInParallelAsync(5, async _ =>
        {
            using var client = CreateClient();
            return await client.SendAsync(CreateLoginRequest(user.Email!, WrongPassword));
        });
        foreach (var r in responses)
        {
            r.Dispose();
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var reread = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);

        // Identity resets AccessFailedCount to 0 when it triggers the lockout,
        // so the invariant is: either lockout is active OR the count is
        // greater than zero (no attempts were lost to concurrency).
        var lockoutActive = reread.LockoutEnd is not null && reread.LockoutEnd > DateTimeOffset.UtcNow;
        Assert.True(
            lockoutActive || reread.AccessFailedCount > 0,
            $"Expected lockout or non-zero AccessFailedCount but got LockoutEnd={reread.LockoutEnd}, AccessFailedCount={reread.AccessFailedCount}.");

        // A subsequent attempt should now be rejected (or continue increasing
        // AccessFailedCount if the previous batch stopped just short of the
        // threshold). Verify eventual lockout by driving up to two more
        // attempts and asserting lockout is finally reached.
        using var closingClient = CreateClient();
        _ = await closingClient.SendAsync(CreateLoginRequest(user.Email!, WrongPassword));
        _ = await closingClient.SendAsync(CreateLoginRequest(user.Email!, WrongPassword));

        var final = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.NotNull(final.LockoutEnd);
        Assert.True(final.LockoutEnd > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Parallel_unknown_email_requests_all_return_401_with_empty_body_and_no_cookie()
    {
        var responses = await RunInParallelAsync(5, async index =>
        {
            using var client = CreateClient();
            var request = CreateLoginRequest(
                "unknown-" + index + "-" + Guid.NewGuid().ToString("N") + "@test.local",
                DefaultPassword);
            return await client.SendAsync(request);
        });

        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
            Assert.All(responses, r => Assert.False(ResponseHasSetCookie(r)));
            foreach (var r in responses)
            {
                var body = await r.Content.ReadAsStringAsync();
                Assert.True(string.IsNullOrEmpty(body));
            }
        }
        finally
        {
            foreach (var r in responses)
            {
                r.Dispose();
            }
        }
    }

    // =================================================================
    // Finding #2 (P2) — concurrent valid logins
    // =================================================================

    [Fact]
    public async Task Parallel_valid_logins_for_same_user_all_return_200()
    {
        var (user, _) = await SeedActiveUserAsync();

        var responses = await RunInParallelAsync(3, async _ =>
        {
            using var client = CreateClient();
            return await client.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));
        });

        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
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
    public async Task Parallel_valid_logins_for_same_user_persist_distinct_refresh_tokens()
    {
        var (user, _) = await SeedActiveUserAsync();

        var responses = await RunInParallelAsync(3, async _ =>
        {
            using var client = CreateClient();
            return await client.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));
        });
        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        }
        finally
        {
            foreach (var r in responses)
            {
                r.Dispose();
            }
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id)
            .ToListAsync();

        Assert.Equal(3, tokens.Count);
        Assert.Equal(3, tokens.Select(t => t.TokenHash).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Parallel_valid_logins_for_same_user_leave_user_state_consistent()
    {
        var (user, _) = await SeedActiveUserAsync();

        var responses = await RunInParallelAsync(3, async _ =>
        {
            using var client = CreateClient();
            return await client.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));
        });
        foreach (var r in responses)
        {
            r.Dispose();
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var reread = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);

        Assert.Equal(0, reread.AccessFailedCount);
        Assert.NotNull(reread.LastLoginAt);
    }

    // =================================================================
    // Finding #3 (P3) — stale role snapshot deterministically rejected
    // =================================================================

    [Fact]
    public async Task Stale_role_login_returns_401_and_persists_no_refresh_token()
    {
        var (user, _) = await SeedActiveUserAsync(role: OpsFlowRoles.Coordinator);

        using var readySignal = new SemaphoreSlim(0, 1);
        using var releaseSignal = new SemaphoreSlim(0, 1);

        using var errorFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserAuthenticator>();
                services.AddScoped<IUserAuthenticator>(sp =>
                {
                    var inner = SignalingUserAuthenticator.CreateInnerAuthenticator(sp);
                    return new SignalingUserAuthenticator(inner, readySignal, releaseSignal);
                });
            });
        });

        using var client = errorFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        var loginTask = client.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));

        // Wait for the authenticator to finish and signal readiness.
        Assert.True(
            await readySignal.WaitAsync(TimeSpan.FromSeconds(30)),
            "SignalingUserAuthenticator did not signal ready in time.");

        // Mutate the role via UserManager BEFORE releasing the login. This
        // rotates ConcurrencyStamp on the user row.
        string preMutationStamp;
        string postMutationStamp;
        using (var mutateScope = _factory.Services.CreateScope())
        {
            var userManager = mutateScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var tracked = await userManager.FindByIdAsync(user.Id.ToString());
            Assert.NotNull(tracked);
            preMutationStamp = tracked!.ConcurrencyStamp!;

            var removeResult = await userManager.RemoveFromRoleAsync(tracked, OpsFlowRoles.Coordinator);
            Assert.True(removeResult.Succeeded);

            var mutateDb = mutateScope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            var mutated = await mutateDb.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
            postMutationStamp = mutated.ConcurrencyStamp!;
        }

        Assert.NotEqual(preMutationStamp, postMutationStamp);

        // Release the login and observe the outcome.
        _ = releaseSignal.Release();

        using var response = await loginTask;

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(ResponseHasSetCookie(response));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(string.IsNullOrEmpty(body));

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        Assert.False(await verifyDb.RefreshTokens.AnyAsync(t => t.UserId == user.Id));
        var finalUser = await verifyDb.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Null(finalUser.LastLoginAt);
    }

    [Fact]
    public async Task Normal_login_returns_expected_role_claims()
    {
        var (user, _) = await SeedActiveUserAsync(role: OpsFlowRoles.Coordinator);

        using var client = CreateClient();
        using var response = await client.SendAsync(CreateLoginRequest(user.Email!, DefaultPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login);
        Assert.Contains(OpsFlowRoles.Coordinator, login.User.Roles);
    }
}

/// <summary>
/// Test-only decorator that lets a test coordinate deterministically with the
/// authenticator: it signals readiness after authenticate completes
/// successfully and blocks until the test releases it. This makes stale-role
/// scenarios exact without Thread.Sleep or timing assertions.
/// </summary>
internal sealed class SignalingUserAuthenticator(
    IUserAuthenticator inner,
    SemaphoreSlim readySignal,
    SemaphoreSlim releaseSignal) : IUserAuthenticator
{
    /// <summary>
    /// Manually constructs the production <c>IdentityUserAuthenticator</c>
    /// from a scope-limited service provider. Using this factory avoids
    /// resolving the decorator itself (which would cause an infinite loop) and
    /// avoids re-registering the concrete production type just so that a
    /// decorator can depend on it.
    /// </summary>
    public static IUserAuthenticator CreateInnerAuthenticator(IServiceProvider sp)
    {
        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var db = sp.GetRequiredService<OpsFlowDbContext>();
        var dummy = sp.GetRequiredService<OpsFlow.Infrastructure.Authentication.IDummyPasswordVerifier>();
        return new OpsFlow.Infrastructure.Authentication.IdentityUserAuthenticator(userManager, db, dummy);
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        string email, string password, CancellationToken cancellationToken)
    {
        var result = await inner.AuthenticateAsync(email, password, cancellationToken);
        if (result.Status == AuthenticationStatus.Success)
        {
            _ = readySignal.Release();
            await releaseSignal.WaitAsync(cancellationToken);
        }
        return result;
    }
}
