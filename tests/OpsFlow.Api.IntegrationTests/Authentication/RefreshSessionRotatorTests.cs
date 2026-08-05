using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Api.IntegrationTests.TestSupport;
using OpsFlow.Application.Authentication;
using OpsFlow.Application.Authorization;
using OpsFlow.Infrastructure.Authentication;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Authentication;

[Collection(SqlServerCollection.Name)]
public sealed class RefreshSessionRotatorTests
{
    private const string ValidPassword = "CorrectHorse!123456";
    private const string OtherValidPassword = "OtherStrongPass!123456";

    private static readonly DateTimeOffset FixedUtcNow =
        new(2030, 1, 15, 12, 30, 0, TimeSpan.Zero);

    private const int ConfiguredLifetimeDays = 7;

    private readonly SqlServerFixture _fixture;

    public RefreshSessionRotatorTests(SqlServerFixture fixture) => _fixture = fixture;

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

    private static async Task<(ApplicationUser User, string RawToken)> SeedAndLoginAsync(
        AuthenticationTestHost host,
        string password = ValidPassword)
    {
        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, password, role: OpsFlowRoles.Viewer);

        var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();
        var authResult = await authenticator.AuthenticateAsync(
            user.Email!, password, CancellationToken.None);
        Assert.Equal(AuthenticationStatus.Success, authResult.Status);

        var snapshot = authResult.User!;
        var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
        var issueResult = await issuer.IssueAsync(
            new LoginSessionRequest(
                snapshot.UserId,
                snapshot.OrganizationId,
                snapshot.SecurityStamp,
                snapshot.ConcurrencyStamp),
            CancellationToken.None);
        Assert.Equal(SessionIssueStatus.Issued, issueResult.Status);

        return (user, issueResult.RefreshToken!);
    }

    // ================================================================
    // Base rotation behavior
    // ================================================================

    [Fact]
    public async Task Valid_rotation_returns_rotated_with_fresh_access_and_refresh_tokens()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        var (user, rawToken) = await SeedAndLoginAsync(host);

        using var scope = host.Services.CreateScope();
        var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();

        var result = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);

        Assert.Equal(RefreshRotationStatus.Rotated, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(result.NewRefreshToken));
        Assert.NotNull(result.User);
        Assert.Equal(user.Id, result.User.UserId);
        Assert.Contains(OpsFlowRoles.Viewer, result.User.Roles);
    }

    [Fact]
    public async Task Valid_rotation_marks_old_token_as_rotated_and_links_successor()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        var (user, rawToken) = await SeedAndLoginAsync(host);
        var hasher = host.Services.GetRequiredService<IRefreshTokenHasher>();
        var oldHash = hasher.Hash(rawToken);

        using (var scope = host.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            var result = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);
            Assert.Equal(RefreshRotationStatus.Rotated, result.Status);
        }

        await using var db = OpenReadContext();
        var oldToken = await db.RefreshTokens.AsNoTracking()
            .SingleAsync(t => t.TokenHash == oldHash);
        Assert.NotNull(oldToken.RevokedAt);
        Assert.Equal(RefreshTokenRevocationReason.Rotated, oldToken.ReasonRevoked);
        Assert.NotNull(oldToken.ReplacedByTokenId);

        var successor = await db.RefreshTokens.AsNoTracking()
            .SingleAsync(t => t.Id == oldToken.ReplacedByTokenId);
        Assert.Equal(user.Id, successor.UserId);
        Assert.Equal(oldToken.TokenFamilyId, successor.TokenFamilyId);
        Assert.Null(successor.RevokedAt);
        Assert.Equal(FixedUtcNow, successor.CreatedAt);
        Assert.Equal(FixedUtcNow.AddDays(ConfiguredLifetimeDays), successor.ExpiresAt);
    }

    [Fact]
    public async Task Valid_rotation_persists_hashed_successor_never_the_raw_value()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        var (user, rawToken) = await SeedAndLoginAsync(host);

        string newRaw;
        using (var scope = host.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            var result = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);
            Assert.Equal(RefreshRotationStatus.Rotated, result.Status);
            newRaw = result.NewRefreshToken!;
        }

        await using var db = OpenReadContext();
        var successor = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .SingleAsync();
        var hasher = host.Services.GetRequiredService<IRefreshTokenHasher>();
        Assert.NotEqual(newRaw, successor.TokenHash);
        Assert.Equal(hasher.Hash(newRaw), successor.TokenHash);
    }

    [Fact]
    public async Task Successor_token_inherits_family_id()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        var (user, rawToken) = await SeedAndLoginAsync(host);

        Guid originalFamily;
        await using (var db = OpenReadContext())
        {
            originalFamily = (await db.RefreshTokens.AsNoTracking()
                .SingleAsync(t => t.UserId == user.Id)).TokenFamilyId;
        }

        using (var scope = host.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            var result = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);
            Assert.Equal(RefreshRotationStatus.Rotated, result.Status);
        }

        await using var db2 = OpenReadContext();
        var successor = await db2.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .SingleAsync();
        Assert.Equal(originalFamily, successor.TokenFamilyId);
    }

    // ================================================================
    // Rejection paths (all return Rejected, most write no state)
    // ================================================================

    [Fact]
    public async Task Unknown_token_returns_rejected_and_writes_nothing()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        _ = await SeedAndLoginAsync(host);

        using var scope = host.Services.CreateScope();
        var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
        var result = await rotator.RotateAsync(
            new RefreshRotationRequest("totally-unknown-token"), CancellationToken.None);

        Assert.Equal(RefreshRotationStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task Blank_raw_token_returns_rejected()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        using var scope = host.Services.CreateScope();
        var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();

        var result = await rotator.RotateAsync(new RefreshRotationRequest("   "), CancellationToken.None);
        Assert.Equal(RefreshRotationStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task Expired_token_returns_rejected_without_writes()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        var (user, rawToken) = await SeedAndLoginAsync(host);

        // Force the token to appear expired by pushing ExpiresAt into the past.
        var hasher = host.Services.GetRequiredService<IRefreshTokenHasher>();
        var hash = hasher.Hash(rawToken);
        await using (var db = OpenReadContext())
        {
            var token = await db.RefreshTokens.SingleAsync(t => t.TokenHash == hash);
            token.ExpiresAt = FixedUtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        using (var scope = host.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            var result = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);
            Assert.Equal(RefreshRotationStatus.Rejected, result.Status);
        }

        await using var db2 = OpenReadContext();
        Assert.Equal(1, await db2.RefreshTokens.CountAsync(t => t.UserId == user.Id));
    }

    [Fact]
    public async Task Inactive_user_after_login_returns_rejected()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        var (user, rawToken) = await SeedAndLoginAsync(host);

        await using (var db = OpenReadContext())
        {
            var tracked = await db.Users.SingleAsync(u => u.Id == user.Id);
            tracked.IsActive = false;
            await db.SaveChangesAsync();
        }

        using var scope = host.Services.CreateScope();
        var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
        var result = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);
        Assert.Equal(RefreshRotationStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task Inactive_organization_after_login_returns_rejected()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        var (user, rawToken) = await SeedAndLoginAsync(host);

        await using (var db = OpenReadContext())
        {
            var tracked = await db.Users.SingleAsync(u => u.Id == user.Id);
            var org = await db.Organizations.SingleAsync(o => o.Id == tracked.OrganizationId);
            org.IsActive = false;
            await db.SaveChangesAsync();
        }

        using var scope = host.Services.CreateScope();
        var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
        var result = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);
        Assert.Equal(RefreshRotationStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task Locked_user_returns_rejected()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        var (user, rawToken) = await SeedAndLoginAsync(host);

        await using (var db = OpenReadContext())
        {
            var tracked = await db.Users.SingleAsync(u => u.Id == user.Id);
            tracked.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
            await db.SaveChangesAsync();
        }

        using var scope = host.Services.CreateScope();
        var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
        var result = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);
        Assert.Equal(RefreshRotationStatus.Rejected, result.Status);
    }

    // ================================================================
    // Replay / concurrent-race distinction
    // ================================================================

    [Fact]
    public async Task Replaying_an_already_rotated_token_revokes_the_family_with_ReuseDetected()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        var (user, rawToken) = await SeedAndLoginAsync(host);

        using (var scope = host.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            var first = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);
            Assert.Equal(RefreshRotationStatus.Rotated, first.Status);
        }

        using (var scope = host.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            var replay = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);
            Assert.Equal(RefreshRotationStatus.Rejected, replay.Status);
        }

        await using var db = OpenReadContext();
        var familyTokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id)
            .ToListAsync();
        Assert.All(familyTokens, t => Assert.NotNull(t.RevokedAt));
        Assert.Contains(familyTokens,
            t => t.ReasonRevoked == RefreshTokenRevocationReason.ReuseDetected);
    }

    // ================================================================
    // SecurityStamp binding
    // ================================================================

    [Fact]
    public async Task Password_change_after_login_causes_refresh_rejection_with_SecurityChange_family_revocation()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        var (user, rawToken) = await SeedAndLoginAsync(host);

        using (var scope = host.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var tracked = await userManager.FindByIdAsync(user.Id.ToString());
            Assert.NotNull(tracked);
            var result = await userManager.ChangePasswordAsync(tracked!, ValidPassword, OtherValidPassword);
            Assert.True(result.Succeeded);
        }

        using (var scope = host.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            var result = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);
            Assert.Equal(RefreshRotationStatus.Rejected, result.Status);
        }

        await using var db = OpenReadContext();
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id).ToListAsync();
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
        Assert.All(tokens, t =>
            Assert.Equal(RefreshTokenRevocationReason.SecurityChange, t.ReasonRevoked));
    }

    [Fact]
    public async Task Role_change_after_login_does_NOT_reject_refresh_and_new_JWT_gets_current_roles()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        var (user, rawToken) = await SeedAndLoginAsync(host);

        using (var scope = host.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roleManager.RoleExistsAsync(OpsFlowRoles.Coordinator))
            {
                var roleResult = await roleManager.CreateAsync(
                    new IdentityRole<Guid>(OpsFlowRoles.Coordinator) { Id = Guid.NewGuid() });
                Assert.True(roleResult.Succeeded);
            }
            var tracked = await userManager.FindByIdAsync(user.Id.ToString());
            Assert.NotNull(tracked);
            var addResult = await userManager.AddToRoleAsync(tracked!, OpsFlowRoles.Coordinator);
            Assert.True(addResult.Succeeded);
        }

        using var scope2 = host.Services.CreateScope();
        var rotator = scope2.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
        var result = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);
        Assert.Equal(RefreshRotationStatus.Rotated, result.Status);
        Assert.Contains(OpsFlowRoles.Coordinator, result.User!.Roles);
        Assert.Contains(OpsFlowRoles.Viewer, result.User.Roles);
    }

    [Fact]
    public async Task Legacy_token_with_null_IssuedSecurityStamp_is_rejected_with_family_revocation()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        var (user, rawToken) = await SeedAndLoginAsync(host);

        var hasher = host.Services.GetRequiredService<IRefreshTokenHasher>();
        var hash = hasher.Hash(rawToken);

        // Simulate a legacy row that predates the IssuedSecurityStamp field.
        await using (var db = OpenReadContext())
        {
            var token = await db.RefreshTokens.SingleAsync(t => t.TokenHash == hash);
            token.IssuedSecurityStamp = null;
            await db.SaveChangesAsync();
        }

        using (var scope = host.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            var result = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);
            Assert.Equal(RefreshRotationStatus.Rejected, result.Status);
        }

        await using var db2 = OpenReadContext();
        var tokens = await db2.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id).ToListAsync();
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
        Assert.All(tokens, t =>
            Assert.Equal(RefreshTokenRevocationReason.SecurityChange, t.ReasonRevoked));
    }

    [Fact]
    public async Task Other_family_tokens_are_not_affected_by_reuse_family_revocation()
    {
        await using var host = BuildHost(_fixture.ConnectionString);
        var (user, rawToken) = await SeedAndLoginAsync(host);

        // Create a second family by seeding another login for the same user
        // (through the LoginSessionIssuer path).
        string otherFamilyRawToken;
        using (var scope = host.Services.CreateScope())
        {
            var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();
            var authResult = await authenticator.AuthenticateAsync(
                user.Email!, ValidPassword, CancellationToken.None);
            var snapshot = authResult.User!;
            var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
            var issue = await issuer.IssueAsync(
                new LoginSessionRequest(
                    snapshot.UserId, snapshot.OrganizationId,
                    snapshot.SecurityStamp, snapshot.ConcurrencyStamp),
                CancellationToken.None);
            otherFamilyRawToken = issue.RefreshToken!;
        }

        // Rotate the first token, then replay it to trigger reuse detection.
        using (var scope = host.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            _ = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);
        }
        using (var scope = host.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            _ = await rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None);
        }

        var hasher = host.Services.GetRequiredService<IRefreshTokenHasher>();
        var otherHash = hasher.Hash(otherFamilyRawToken);
        await using var db = OpenReadContext();
        var otherToken = await db.RefreshTokens.AsNoTracking()
            .SingleAsync(t => t.TokenHash == otherHash);
        Assert.Null(otherToken.RevokedAt);
    }

    // ================================================================
    // JWT-generation failure rolls back the entire rotation transaction
    // ================================================================

    [Fact]
    public async Task Access_token_generation_failure_rolls_back_refresh_rotation()
    {
        // Real SQL Server, real RefreshSessionRotator, real transaction.
        // The only substitution is IAccessTokenService, which throws a
        // controlled exception from inside the rotator's transaction —
        // AFTER validation and rotation writes have been staged in memory
        // but BEFORE SaveChangesAsync / CommitAsync.
        var thrown = new InvalidOperationException("Simulated access-token failure.");
        var throwingService = new ThrowingAccessTokenService(thrown);

        await using var host = AuthenticationTestHost.Build(
            _fixture.ConnectionString,
            clock: new FixedClock(FixedUtcNow),
            accessTokenService: throwingService);

        var (user, rawToken) = await SeedAndLoginAsync(host);
        var hasher = host.Services.GetRequiredService<IRefreshTokenHasher>();
        var oldHash = hasher.Hash(rawToken);

        RefreshToken preRotation;
        int preRotationFamilyCount;
        await using (var pre = OpenReadContext())
        {
            preRotation = await pre.RefreshTokens.AsNoTracking()
                .SingleAsync(t => t.TokenHash == oldHash);
            preRotationFamilyCount = await pre.RefreshTokens.AsNoTracking()
                .CountAsync(t => t.TokenFamilyId == preRotation.TokenFamilyId);
        }
        Assert.Null(preRotation.RevokedAt);
        Assert.Null(preRotation.ReasonRevoked);
        Assert.Null(preRotation.ReplacedByTokenId);
        Assert.Equal(1, preRotationFamilyCount);

        // Invoke the real rotator inside a fresh scope, expect the exception
        // to propagate out.
        using (var scope = host.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            var caught = await Assert.ThrowsAsync<InvalidOperationException>(
                () => rotator.RotateAsync(new RefreshRotationRequest(rawToken), CancellationToken.None));
            Assert.Same(thrown, caught);
        }

        Assert.Equal(1, throwingService.CallCount);

        // Fresh DbContext to bypass any in-memory tracker state.
        await using var verify = OpenReadContext();
        var postRotation = await verify.RefreshTokens.AsNoTracking()
            .SingleOrDefaultAsync(t => t.TokenHash == oldHash);

        // Original token must still exist and remain untouched.
        Assert.NotNull(postRotation);
        Assert.Equal(preRotation.Id, postRotation!.Id);
        Assert.Null(postRotation.RevokedAt);
        Assert.Null(postRotation.ReasonRevoked);
        Assert.Null(postRotation.ReplacedByTokenId);
        Assert.Equal(preRotation.TokenFamilyId, postRotation.TokenFamilyId);
        Assert.Equal(preRotation.IssuedSecurityStamp, postRotation.IssuedSecurityStamp,
            StringComparer.Ordinal);

        // No successor token was persisted; family size unchanged.
        var postFamilyCount = await verify.RefreshTokens.AsNoTracking()
            .CountAsync(t => t.TokenFamilyId == preRotation.TokenFamilyId);
        Assert.Equal(1, postFamilyCount);

        // The user's total refresh-token count for this user is also
        // unchanged (defense-in-depth against a stray insert).
        var totalForUser = await verify.RefreshTokens.AsNoTracking()
            .CountAsync(t => t.UserId == user.Id);
        Assert.Equal(1, totalForUser);

        // The original token remains usable from the persistence perspective:
        // a subsequent rotation using the same raw token (with a real access
        // token service, i.e. a fresh host) succeeds.
        await using var successHost = AuthenticationTestHost.Build(
            _fixture.ConnectionString,
            clock: new FixedClock(FixedUtcNow));

        using var successScope = successHost.Services.CreateScope();
        var successRotator = successScope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
        var successResult = await successRotator.RotateAsync(
            new RefreshRotationRequest(rawToken), CancellationToken.None);
        Assert.Equal(RefreshRotationStatus.Rotated, successResult.Status);
    }

    // ================================================================
    // Regression: expiration must use the post-lock authoritative time,
    // not the pre-lock initial timestamp. Simulates the "waited on the
    // auth locks long enough for the token to expire" scenario without
    // Thread.Sleep or real lock blocking by injecting a clock that
    // returns an active-window value at the initial observation and an
    // expired-window value at the post-lock validation observation.
    // ================================================================

    [Fact]
    public async Task Refresh_uses_post_lock_time_and_rejects_token_that_expired_while_waiting_on_locks()
    {
        // Setup host: seed + login use a FixedClock so ExpiresAt is
        // predictable.
        var loginNow = FixedUtcNow;
        var expectedExpiresAt = loginNow.AddDays(ConfiguredLifetimeDays);

        await using var setupHost = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: new FixedClock(loginNow));
        var (user, rawToken) = await SeedAndLoginAsync(setupHost);

        var hasher = setupHost.Services.GetRequiredService<IRefreshTokenHasher>();
        var hash = hasher.Hash(rawToken);

        RefreshToken preRotation;
        int preFamilyCount;
        await using (var pre = OpenReadContext())
        {
            preRotation = await pre.RefreshTokens.AsNoTracking()
                .SingleAsync(t => t.TokenHash == hash);
            preFamilyCount = await pre.RefreshTokens.AsNoTracking()
                .CountAsync(t => t.TokenFamilyId == preRotation.TokenFamilyId);
        }
        Assert.Equal(expectedExpiresAt, preRotation.ExpiresAt);
        Assert.Null(preRotation.RevokedAt);

        // Rotation host: QueuedClock returns two distinct values.
        //   - initialNow  = still within the token's active window
        //   - validationNow = past ExpiresAt (as if we waited long enough
        //                     on the auth locks for the token to expire)
        var initialNow = expectedExpiresAt.AddHours(-1);
        var validationNow = expectedExpiresAt.AddMinutes(1);
        var queuedClock = new QueuedClock(initialNow, validationNow);

        await using var rotationHost = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: queuedClock);

        using (var scope = rotationHost.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            var result = await rotator.RotateAsync(
                new RefreshRotationRequest(rawToken), CancellationToken.None);

            Assert.Equal(RefreshRotationStatus.Rejected, result.Status);
            Assert.Null(result.NewRefreshToken);
            Assert.Null(result.AccessToken);
        }

        // The clock was consulted at both boundaries.
        Assert.True(queuedClock.CallCount >= 2,
            $"Expected at least two clock reads (initialNow + validationNow), got {queuedClock.CallCount}.");

        // Fresh DbContext: assert the original token was NOT rotated and
        // no successor was persisted.
        await using var verify = OpenReadContext();
        var postRotation = await verify.RefreshTokens.AsNoTracking()
            .SingleOrDefaultAsync(t => t.TokenHash == hash);
        Assert.NotNull(postRotation);
        Assert.Equal(preRotation.Id, postRotation!.Id);
        Assert.Null(postRotation.RevokedAt);
        Assert.Null(postRotation.ReasonRevoked);
        Assert.Null(postRotation.ReplacedByTokenId);
        Assert.Equal(preRotation.TokenFamilyId, postRotation.TokenFamilyId);

        var postFamilyCount = await verify.RefreshTokens.AsNoTracking()
            .CountAsync(t => t.TokenFamilyId == preRotation.TokenFamilyId);
        Assert.Equal(preFamilyCount, postFamilyCount);

        var totalForUser = await verify.RefreshTokens.AsNoTracking()
            .CountAsync(t => t.UserId == user.Id);
        Assert.Equal(1, totalForUser);
    }

    // ================================================================
    // Ordinary successful rotation must persist post-lock (validationNow)
    // timestamps, not the pre-lock (initialNow) value.
    // ================================================================

    [Fact]
    public async Task Successful_rotation_persists_validation_time_not_initial_time()
    {
        var loginNow = FixedUtcNow;

        await using var setupHost = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: new FixedClock(loginNow));
        var (user, rawToken) = await SeedAndLoginAsync(setupHost);

        // initialNow and validationNow are BOTH within the active window
        // (so the token does not expire), but are different values so we
        // can verify which one was actually persisted.
        var initialNow = loginNow.AddHours(1);
        var validationNow = loginNow.AddHours(2);
        var queuedClock = new QueuedClock(initialNow, validationNow);
        var expectedNewExpiresAt = validationNow.AddDays(ConfiguredLifetimeDays);

        await using var rotationHost = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: queuedClock);

        var hasher = rotationHost.Services.GetRequiredService<IRefreshTokenHasher>();
        var oldHash = hasher.Hash(rawToken);

        RefreshRotationResult result;
        using (var scope = rotationHost.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            result = await rotator.RotateAsync(
                new RefreshRotationRequest(rawToken), CancellationToken.None);
            Assert.Equal(RefreshRotationStatus.Rotated, result.Status);
        }

        Assert.Equal(expectedNewExpiresAt, result.NewRefreshTokenExpiresAt);

        await using var verify = OpenReadContext();
        var oldToken = await verify.RefreshTokens.AsNoTracking()
            .SingleAsync(t => t.TokenHash == oldHash);
        Assert.Equal(validationNow, oldToken.RevokedAt);
        Assert.NotEqual(initialNow, oldToken.RevokedAt);

        var successor = await verify.RefreshTokens.AsNoTracking()
            .SingleAsync(t => t.UserId == user.Id && t.RevokedAt == null);
        Assert.Equal(validationNow, successor.CreatedAt);
        Assert.Equal(expectedNewExpiresAt, successor.ExpiresAt);
        Assert.NotEqual(initialNow, successor.CreatedAt);
    }
}
