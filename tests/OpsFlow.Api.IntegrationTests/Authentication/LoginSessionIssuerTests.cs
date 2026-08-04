using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Api.IntegrationTests.TestSupport;
using OpsFlow.Application.Authentication;
using OpsFlow.Infrastructure.Authentication;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Authentication;

[Collection(SqlServerCollection.Name)]
public sealed class LoginSessionIssuerTests
{
    private const string ValidPassword = "CorrectHorse!123456";
    private const string WrongPassword = "WrongHorse!123456";

    private static readonly DateTimeOffset FixedUtcNow =
        new(2030, 1, 15, 12, 30, 0, TimeSpan.Zero);

    private const int ConfiguredLifetimeDays = 7;

    private readonly SqlServerFixture _fixture;

    public LoginSessionIssuerTests(SqlServerFixture fixture) => _fixture = fixture;

    private static string CreateUniqueRawToken(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}";

    private OpsFlowDbContext OpenReadContext() =>
        new(new DbContextOptionsBuilder<OpsFlowDbContext>()
            .UseSqlServer(_fixture.ConnectionString).Options);

    private static AuthenticationTestHost BuildHost(
        string connectionString,
        IRefreshTokenGenerator? tokenGenerator = null) =>
        AuthenticationTestHost.Build(
            connectionString,
            clock: new FixedClock(FixedUtcNow),
            tokenGenerator: tokenGenerator);

    private static async Task<(AuthenticatedUser Snapshot, LoginSessionRequest Request)>
        AuthenticateAsync(IServiceProvider scope, string email) =>
        await AuthenticateAsync(scope, email, ValidPassword);

    private static async Task<(AuthenticatedUser Snapshot, LoginSessionRequest Request)>
        AuthenticateAsync(IServiceProvider scope, string email, string password)
    {
        var authenticator = scope.GetRequiredService<IUserAuthenticator>();
        var result = await authenticator.AuthenticateAsync(email, password, CancellationToken.None);

        Assert.Equal(AuthenticationStatus.Success, result.Status);
        var snapshot = result.User!;
        var request = new LoginSessionRequest(
            snapshot.UserId,
            snapshot.OrganizationId,
            snapshot.SecurityStamp,
            snapshot.ConcurrencyStamp);
        return (snapshot, request);
    }

    // ---------------------------------------------------------------
    // 1. Valid authenticated snapshot -> Issued.
    // ---------------------------------------------------------------
    [Fact]
    public async Task Valid_authenticated_snapshot_issues_a_session()
    {
        await using var host = BuildHost(_fixture.ConnectionString);

        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, ValidPassword);

        var (_, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);
        var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
        var result = await issuer.IssueAsync(request, CancellationToken.None);

        Assert.Equal(SessionIssueStatus.Issued, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.RefreshToken));
        Assert.NotNull(result.RefreshTokenExpiresAt);
    }

    // ---------------------------------------------------------------
    // 2. Persisted RefreshToken stores hash, not raw refresh token.
    // ---------------------------------------------------------------
    [Fact]
    public async Task Persisted_refresh_token_stores_hash_not_raw_token()
    {
        await using var host = BuildHost(_fixture.ConnectionString);

        Guid userId;
        string rawToken;
        using (var scope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
            var user = await AuthenticationTestHost.SeedUserAsync(
                scope.ServiceProvider, org.Id, ValidPassword);
            userId = user.Id;

            var (_, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);
            var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
            var result = await issuer.IssueAsync(request, CancellationToken.None);

            Assert.Equal(SessionIssueStatus.Issued, result.Status);
            rawToken = result.RefreshToken!;
        }

        await using var db = OpenReadContext();
        var token = await db.RefreshTokens.AsNoTracking()
            .SingleAsync(t => t.UserId == userId);

        Assert.NotEqual(rawToken, token.TokenHash);
    }

    // ---------------------------------------------------------------
    // 3. TokenFamilyId is populated and not Guid.Empty.
    // ---------------------------------------------------------------
    [Fact]
    public async Task Token_family_id_is_populated_and_not_empty()
    {
        await using var host = BuildHost(_fixture.ConnectionString);

        Guid userId;
        using (var scope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
            var user = await AuthenticationTestHost.SeedUserAsync(
                scope.ServiceProvider, org.Id, ValidPassword);
            userId = user.Id;

            var (_, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);
            var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
            await issuer.IssueAsync(request, CancellationToken.None);
        }

        await using var db = OpenReadContext();
        var token = await db.RefreshTokens.AsNoTracking()
            .SingleAsync(t => t.UserId == userId);

        Assert.NotEqual(Guid.Empty, token.TokenFamilyId);
    }

    // ---------------------------------------------------------------
    // 4. CreatedAt equals deterministic IClock.UtcNow and ExpiresAt
    //    equals CreatedAt + configured RefreshTokenLifetimeDays.
    // ---------------------------------------------------------------
    [Fact]
    public async Task Created_at_and_expires_at_use_deterministic_clock_and_configured_lifetime()
    {
        await using var host = BuildHost(_fixture.ConnectionString);

        Guid userId;
        DateTimeOffset returnedExpiresAt;
        using (var scope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
            var user = await AuthenticationTestHost.SeedUserAsync(
                scope.ServiceProvider, org.Id, ValidPassword);
            userId = user.Id;

            var (_, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);
            var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
            var result = await issuer.IssueAsync(request, CancellationToken.None);

            Assert.Equal(SessionIssueStatus.Issued, result.Status);
            returnedExpiresAt = result.RefreshTokenExpiresAt!.Value;
        }

        await using var db = OpenReadContext();
        var token = await db.RefreshTokens.AsNoTracking()
            .SingleAsync(t => t.UserId == userId);

        Assert.Equal(FixedUtcNow, token.CreatedAt);
        Assert.Equal(FixedUtcNow.AddDays(ConfiguredLifetimeDays), token.ExpiresAt);
        Assert.Equal(token.ExpiresAt, returnedExpiresAt);
    }

    // ---------------------------------------------------------------
    // 5. Successful issuance resets a non-zero AccessFailedCount.
    // ---------------------------------------------------------------
    [Fact]
    public async Task Successful_issuance_resets_a_non_zero_access_failed_count()
    {
        await using var host = BuildHost(_fixture.ConnectionString);

        Guid userId;
        using (var scope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
            var user = await AuthenticationTestHost.SeedUserAsync(
                scope.ServiceProvider, org.Id, ValidPassword);
            userId = user.Id;

            var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();
            await authenticator.AuthenticateAsync(user.Email!, WrongPassword, CancellationToken.None);
            await authenticator.AuthenticateAsync(user.Email!, WrongPassword, CancellationToken.None);

            var (_, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);
            var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
            var result = await issuer.IssueAsync(request, CancellationToken.None);
            Assert.Equal(SessionIssueStatus.Issued, result.Status);
        }

        await using var db = OpenReadContext();
        var reread = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal(0, reread.AccessFailedCount);
    }

    // ---------------------------------------------------------------
    // 6. Successful issuance updates LastLoginAt to deterministic
    //    clock value.
    // ---------------------------------------------------------------
    [Fact]
    public async Task Successful_issuance_updates_last_login_at_to_deterministic_clock_value()
    {
        await using var host = BuildHost(_fixture.ConnectionString);

        Guid userId;
        using (var scope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
            var user = await AuthenticationTestHost.SeedUserAsync(
                scope.ServiceProvider, org.Id, ValidPassword);
            userId = user.Id;

            var (_, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);
            var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
            await issuer.IssueAsync(request, CancellationToken.None);
        }

        await using var db = OpenReadContext();
        var reread = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal(FixedUtcNow, reread.LastLoginAt);
    }

    // ---------------------------------------------------------------
    // 7. User deactivated AFTER authentication -> Rejected.
    //    (same-scope stale-tracking pattern)
    // ---------------------------------------------------------------
    [Fact]
    public async Task User_deactivated_after_authentication_is_rejected()
    {
        await using var host = BuildHost(_fixture.ConnectionString);

        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, ValidPassword);

        var (_, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);

        await using (var ext = OpenReadContext())
        {
            var tracked = await ext.Users.SingleAsync(u => u.Id == user.Id);
            tracked.IsActive = false;
            await ext.SaveChangesAsync();
        }

        var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
        var result = await issuer.IssueAsync(request, CancellationToken.None);

        Assert.Equal(SessionIssueStatus.Rejected, result.Status);
        Assert.Null(result.RefreshToken);
    }

    // ---------------------------------------------------------------
    // 8. Organization deactivated AFTER authentication -> Rejected.
    //    (same-scope pattern for consistency)
    // ---------------------------------------------------------------
    [Fact]
    public async Task Organization_deactivated_after_authentication_is_rejected()
    {
        await using var host = BuildHost(_fixture.ConnectionString);

        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, ValidPassword);

        var (_, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);

        await using (var ext = OpenReadContext())
        {
            var tracked = await ext.Organizations.SingleAsync(o => o.Id == org.Id);
            tracked.IsActive = false;
            await ext.SaveChangesAsync();
        }

        var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
        var result = await issuer.IssueAsync(request, CancellationToken.None);

        Assert.Equal(SessionIssueStatus.Rejected, result.Status);
    }

    // ---------------------------------------------------------------
    // 9. User OrganizationId changed AFTER authentication -> Rejected.
    //    (same-scope stale-tracking pattern)
    // ---------------------------------------------------------------
    [Fact]
    public async Task User_organization_changed_after_authentication_is_rejected()
    {
        await using var host = BuildHost(_fixture.ConnectionString);

        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, ValidPassword);

        var (_, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);

        var otherOrg = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        await using (var ext = OpenReadContext())
        {
            var tracked = await ext.Users.SingleAsync(u => u.Id == user.Id);
            tracked.OrganizationId = otherOrg.Id;
            await ext.SaveChangesAsync();
        }

        var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
        var result = await issuer.IssueAsync(request, CancellationToken.None);

        Assert.Equal(SessionIssueStatus.Rejected, result.Status);
    }

    // ---------------------------------------------------------------
    // 10. Lockout introduced AFTER authentication -> Rejected.
    //     (same-scope stale-tracking pattern)
    // ---------------------------------------------------------------
    [Fact]
    public async Task Lockout_introduced_after_authentication_is_rejected()
    {
        await using var host = BuildHost(_fixture.ConnectionString);

        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, ValidPassword);

        var (_, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);

        await using (var ext = OpenReadContext())
        {
            var tracked = await ext.Users.SingleAsync(u => u.Id == user.Id);
            tracked.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
            await ext.SaveChangesAsync();
        }

        var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
        var result = await issuer.IssueAsync(request, CancellationToken.None);

        Assert.Equal(SessionIssueStatus.Rejected, result.Status);
    }

    // ---------------------------------------------------------------
    // 11. SecurityStamp changed AFTER authentication -> Rejected.
    //     (same-scope stale-tracking pattern)
    // ---------------------------------------------------------------
    [Fact]
    public async Task Security_stamp_changed_after_authentication_is_rejected()
    {
        await using var host = BuildHost(_fixture.ConnectionString);

        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, ValidPassword);

        var (_, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);

        await using (var ext = OpenReadContext())
        {
            var tracked = await ext.Users.SingleAsync(u => u.Id == user.Id);
            tracked.SecurityStamp = Guid.NewGuid().ToString();
            await ext.SaveChangesAsync();
        }

        var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
        var result = await issuer.IssueAsync(request, CancellationToken.None);

        Assert.Equal(SessionIssueStatus.Rejected, result.Status);
    }

    // ---------------------------------------------------------------
    // 12. Rejected issuance creates no RefreshToken and no
    //     successful-login writes. (strengthened per adjustment #2)
    // ---------------------------------------------------------------
    [Fact]
    public async Task Rejected_issuance_creates_no_refresh_token_and_no_successful_login_writes()
    {
        await using var host = BuildHost(_fixture.ConnectionString);

        Guid userId;
        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, ValidPassword);
        userId = user.Id;

        var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();
        await authenticator.AuthenticateAsync(user.Email!, WrongPassword, CancellationToken.None);
        await authenticator.AuthenticateAsync(user.Email!, WrongPassword, CancellationToken.None);

        await using (var preCheck = OpenReadContext())
        {
            var pre = await preCheck.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
            Assert.Equal(2, pre.AccessFailedCount);
            Assert.Null(pre.LastLoginAt);
            Assert.False(await preCheck.RefreshTokens.AnyAsync(t => t.UserId == userId));
        }

        var (_, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);

        await using (var ext = OpenReadContext())
        {
            var tracked = await ext.Users.SingleAsync(u => u.Id == userId);
            tracked.IsActive = false;
            await ext.SaveChangesAsync();
        }

        var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
        var result = await issuer.IssueAsync(request, CancellationToken.None);

        Assert.Equal(SessionIssueStatus.Rejected, result.Status);

        await using var db = OpenReadContext();
        var reread = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal(2, reread.AccessFailedCount);
        Assert.Null(reread.LastLoginAt);
        Assert.False(await db.RefreshTokens.AnyAsync(t => t.UserId == userId));
    }

    // ---------------------------------------------------------------
    // 13. Raw refresh token != persisted TokenHash.
    // ---------------------------------------------------------------
    [Fact]
    public async Task Raw_refresh_token_differs_from_persisted_token_hash()
    {
        var fixedToken = CreateUniqueRawToken("raw-diff");
        await using var host = BuildHost(
            _fixture.ConnectionString,
            tokenGenerator: new FixedRefreshTokenGenerator(fixedToken));

        Guid userId;
        string rawToken;
        using (var scope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
            var user = await AuthenticationTestHost.SeedUserAsync(
                scope.ServiceProvider, org.Id, ValidPassword);
            userId = user.Id;

            var (_, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);
            var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
            var result = await issuer.IssueAsync(request, CancellationToken.None);

            Assert.Equal(SessionIssueStatus.Issued, result.Status);
            rawToken = result.RefreshToken!;
        }

        await using var db = OpenReadContext();
        var token = await db.RefreshTokens.AsNoTracking()
            .SingleAsync(t => t.UserId == userId);

        Assert.Equal(fixedToken, rawToken);
        Assert.NotEqual(rawToken, token.TokenHash);
    }

    // ---------------------------------------------------------------
    // 14. persisted TokenHash == IRefreshTokenHasher.Hash(raw token).
    // ---------------------------------------------------------------
    [Fact]
    public async Task Persisted_token_hash_equals_hasher_output_for_returned_raw_token()
    {
        var fixedToken = CreateUniqueRawToken("hash-verify");
        await using var host = BuildHost(
            _fixture.ConnectionString,
            tokenGenerator: new FixedRefreshTokenGenerator(fixedToken));

        Guid userId;
        string expectedHash;
        using (var scope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
            var user = await AuthenticationTestHost.SeedUserAsync(
                scope.ServiceProvider, org.Id, ValidPassword);
            userId = user.Id;

            var hasher = scope.ServiceProvider.GetRequiredService<IRefreshTokenHasher>();
            expectedHash = hasher.Hash(fixedToken);

            var (_, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);
            var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
            var result = await issuer.IssueAsync(request, CancellationToken.None);
            Assert.Equal(SessionIssueStatus.Issued, result.Status);
        }

        await using var db = OpenReadContext();
        var token = await db.RefreshTokens.AsNoTracking()
            .SingleAsync(t => t.UserId == userId);

        Assert.Equal(expectedHash, token.TokenHash);
    }

    // ---------------------------------------------------------------
    // 15. Persistence failure AFTER ResetAccessFailedCountAsync rolls
    //     back ALL successful-login state. (deterministic collision)
    // ---------------------------------------------------------------
    [Fact]
    public async Task Persistence_failure_after_reset_rolls_back_all_successful_login_state()
    {
        var rawToken = CreateUniqueRawToken("rollback");
        await using var host = BuildHost(
            _fixture.ConnectionString,
            tokenGenerator: new FixedRefreshTokenGenerator(rawToken));

        Guid orgId;
        Guid blockerId;
        Guid targetId;
        string targetEmail;

        // Phase A — seed blocker in a dedicated scope, fully persisted.
        using (var blockerScope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(blockerScope.ServiceProvider);
            orgId = org.Id;

            var blocker = await AuthenticationTestHost.SeedUserAsync(
                blockerScope.ServiceProvider, org.Id, ValidPassword);
            blockerId = blocker.Id;

            var hasher = blockerScope.ServiceProvider.GetRequiredService<IRefreshTokenHasher>();
            var collidingHash = hasher.Hash(rawToken);

            var db = blockerScope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            _ = db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = blocker.Id,
                TokenHash = collidingHash,
                TokenFamilyId = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            });
            _ = await db.SaveChangesAsync();
        }

        // Phase B — seed target with non-zero AccessFailedCount.
        using (var targetScope = host.Services.CreateScope())
        {
            var target = await AuthenticationTestHost.SeedUserAsync(
                targetScope.ServiceProvider, orgId, ValidPassword);
            targetId = target.Id;
            targetEmail = target.Email!;

            var authenticator = targetScope.ServiceProvider.GetRequiredService<IUserAuthenticator>();
            _ = await authenticator.AuthenticateAsync(targetEmail, WrongPassword, CancellationToken.None);
            _ = await authenticator.AuthenticateAsync(targetEmail, WrongPassword, CancellationToken.None);
        }

        // Phase C — verify preconditions from independent DbContext.
        await using (var preCheck = OpenReadContext())
        {
            var pre = await preCheck.Users.AsNoTracking().SingleAsync(u => u.Id == targetId);
            Assert.Equal(2, pre.AccessFailedCount);
            Assert.Null(pre.LastLoginAt);
            Assert.False(await preCheck.RefreshTokens.AnyAsync(t => t.UserId == targetId));
        }

        // Phase D — authenticate and issue; expect collision from IssueAsync.
        using (var issueScope = host.Services.CreateScope())
        {
            var (_, request) = await AuthenticateAsync(issueScope.ServiceProvider, targetEmail);

            var issuer = issueScope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
            await Assert.ThrowsAsync<DbUpdateException>(
                () => issuer.IssueAsync(request, CancellationToken.None));
        }

        // Phase E — verify rollback from fresh independent DbContext.
        await using var verify = OpenReadContext();
        var reread = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == targetId);
        Assert.Equal(2, reread.AccessFailedCount);
        Assert.Null(reread.LastLoginAt);
        Assert.False(await verify.RefreshTokens.AnyAsync(t => t.UserId == targetId));
        Assert.True(await verify.RefreshTokens.AnyAsync(t => t.UserId == blockerId));
    }

    // ---------------------------------------------------------------
    // 16. Stale ExpectedConcurrencyStamp -> Rejected and no writes.
    //     Proves the fix for GitHub finding #3: any Identity role /
    //     password / lockout mutation via UserStore.UpdateAsync rotates
    //     ConcurrencyStamp, so the pre-minted authenticated snapshot is
    //     considered stale and no session state is persisted.
    // ---------------------------------------------------------------
    [Fact]
    public async Task Stale_expected_concurrency_stamp_is_rejected_and_persists_no_writes()
    {
        await using var host = BuildHost(_fixture.ConnectionString);

        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, ValidPassword);

        var (snapshot, _) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);
        var staleRequest = new LoginSessionRequest(
            snapshot.UserId,
            snapshot.OrganizationId,
            snapshot.SecurityStamp,
            ExpectedConcurrencyStamp: "stale-concurrency-stamp-value");

        var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
        var result = await issuer.IssueAsync(staleRequest, CancellationToken.None);

        Assert.Equal(SessionIssueStatus.Rejected, result.Status);
        Assert.Null(result.RefreshToken);

        await using var db = OpenReadContext();
        var reread = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Null(reread.LastLoginAt);
        Assert.False(await db.RefreshTokens.AnyAsync(t => t.UserId == user.Id));
    }

    // ---------------------------------------------------------------
    // 17. Successful issuance resets AccessFailedCount to 0 WITHOUT
    //     rotating ConcurrencyStamp. Proves the direct tracked-property
    //     assignment fix that lets concurrent successful logins from the
    //     same authenticated snapshot both complete instead of
    //     self-invalidating each other.
    // ---------------------------------------------------------------
    [Fact]
    public async Task Successful_issuance_resets_access_failed_count_without_rotating_concurrency_stamp()
    {
        await using var host = BuildHost(_fixture.ConnectionString);

        Guid userId;
        string preIssueConcurrencyStamp;
        using (var scope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
            var user = await AuthenticationTestHost.SeedUserAsync(
                scope.ServiceProvider, org.Id, ValidPassword);
            userId = user.Id;

            var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();
            _ = await authenticator.AuthenticateAsync(user.Email!, WrongPassword, CancellationToken.None);
            _ = await authenticator.AuthenticateAsync(user.Email!, WrongPassword, CancellationToken.None);

            var (snapshot, request) = await AuthenticateAsync(scope.ServiceProvider, user.Email!);
            preIssueConcurrencyStamp = snapshot.ConcurrencyStamp;

            var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
            var result = await issuer.IssueAsync(request, CancellationToken.None);
            Assert.Equal(SessionIssueStatus.Issued, result.Status);
        }

        await using var db = OpenReadContext();
        var reread = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal(0, reread.AccessFailedCount);
        Assert.Equal(
            preIssueConcurrencyStamp,
            reread.ConcurrencyStamp,
            StringComparer.Ordinal);
    }
}
