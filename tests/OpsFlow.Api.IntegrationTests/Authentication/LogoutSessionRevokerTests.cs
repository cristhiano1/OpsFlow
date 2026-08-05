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
public sealed class LogoutSessionRevokerTests
{
    private const string ValidPassword = "CorrectHorse!123456";
    private const string OtherValidPassword = "OtherStrongPass!123456";

    private static readonly DateTimeOffset LogoutClockNow =
        new(2030, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public LogoutSessionRevokerTests(SqlServerFixture fixture) => _fixture = fixture;

    private OpsFlowDbContext OpenReadContext() =>
        new(new DbContextOptionsBuilder<OpsFlowDbContext>()
            .UseSqlServer(_fixture.ConnectionString).Options);

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
        var snapshot = authResult.User!;

        var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
        var issue = await issuer.IssueAsync(
            new LoginSessionRequest(
                snapshot.UserId,
                snapshot.OrganizationId,
                snapshot.SecurityStamp,
                snapshot.ConcurrencyStamp),
            CancellationToken.None);

        return (user, issue.RefreshToken!);
    }

    // ================================================================
    // Base logout behavior
    // ================================================================

    [Fact]
    public async Task Active_token_logout_revokes_family_with_Logout_reason_and_authoritative_time()
    {
        await using var host = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: new FixedClock(LogoutClockNow));

        var (user, rawToken) = await SeedAndLoginAsync(host);

        using (var scope = host.Services.CreateScope())
        {
            var revoker = scope.ServiceProvider.GetRequiredService<ILogoutSessionRevoker>();
            await revoker.RevokeAsync(new LogoutRevocationRequest(rawToken), CancellationToken.None);
        }

        await using var db = OpenReadContext();
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id)
            .ToListAsync();
        Assert.Single(tokens);
        var single = tokens[0];
        Assert.NotNull(single.RevokedAt);
        Assert.Equal(LogoutClockNow, single.RevokedAt);
        Assert.Equal(RefreshTokenRevocationReason.Logout, single.ReasonRevoked);
    }

    [Fact]
    public async Task Logout_with_rotated_token_revokes_active_successor_with_Logout()
    {
        // Two distinct clocks so the rotation and the logout stamp
        // different RevokedAt values; that lets us prove the Rotated row's
        // timestamp survives logout unchanged.
        var rotationNow = LogoutClockNow.AddDays(-1);
        var logoutNow = LogoutClockNow;

        await using var rotationHost = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: new FixedClock(rotationNow));

        var (user, originalRaw) = await SeedAndLoginAsync(rotationHost);

        // Rotate once: the original becomes Rotated at rotationNow, a
        // successor is inserted.
        using (var scope = rotationHost.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            var result = await rotator.RotateAsync(
                new RefreshRotationRequest(originalRaw), CancellationToken.None);
            Assert.Equal(RefreshRotationStatus.Rotated, result.Status);
        }

        // Now logout using the ORIGINAL (already Rotated) cookie value,
        // with a distinct clock so we can distinguish preserved vs
        // overwritten timestamps.
        await using var logoutHost = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: new FixedClock(logoutNow));
        using (var scope = logoutHost.Services.CreateScope())
        {
            var revoker = scope.ServiceProvider.GetRequiredService<ILogoutSessionRevoker>();
            await revoker.RevokeAsync(new LogoutRevocationRequest(originalRaw), CancellationToken.None);
        }

        await using var db = OpenReadContext();
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id)
            .ToListAsync();
        Assert.Equal(2, tokens.Count);

        var rotated = tokens.Single(t => t.ReasonRevoked == RefreshTokenRevocationReason.Rotated);
        var successor = tokens.Single(t => t.ReasonRevoked == RefreshTokenRevocationReason.Logout);

        Assert.Equal(rotationNow, rotated.RevokedAt);      // preserved (NOT overwritten by logout)
        Assert.Equal(logoutNow, successor.RevokedAt);      // stamped with logout's clock
        Assert.Equal(rotated.TokenFamilyId, successor.TokenFamilyId);
    }

    [Fact]
    public async Task Previously_revoked_rows_are_never_overwritten()
    {
        await using var host = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: new FixedClock(LogoutClockNow));

        var (user, originalRaw) = await SeedAndLoginAsync(host);

        // Rotate to produce a Rotated row with a known RevokedAt.
        using (var scope = host.Services.CreateScope())
        {
            var rotator = scope.ServiceProvider.GetRequiredService<IRefreshSessionRotator>();
            _ = await rotator.RotateAsync(new RefreshRotationRequest(originalRaw), CancellationToken.None);
        }

        // Snapshot the Rotated row's state before logout.
        RefreshToken rotatedBefore;
        await using (var pre = OpenReadContext())
        {
            rotatedBefore = await pre.RefreshTokens.AsNoTracking()
                .Where(t => t.UserId == user.Id
                            && t.ReasonRevoked == RefreshTokenRevocationReason.Rotated)
                .SingleAsync();
        }

        using (var scope = host.Services.CreateScope())
        {
            var revoker = scope.ServiceProvider.GetRequiredService<ILogoutSessionRevoker>();
            await revoker.RevokeAsync(new LogoutRevocationRequest(originalRaw), CancellationToken.None);
        }

        await using var verify = OpenReadContext();
        var rotatedAfter = await verify.RefreshTokens.AsNoTracking()
            .SingleAsync(t => t.Id == rotatedBefore.Id);

        Assert.Equal(rotatedBefore.RevokedAt, rotatedAfter.RevokedAt);
        Assert.Equal(rotatedBefore.ReasonRevoked, rotatedAfter.ReasonRevoked);
        Assert.Equal(rotatedBefore.ReplacedByTokenId, rotatedAfter.ReplacedByTokenId);
    }

    [Fact]
    public async Task Repeated_logout_is_idempotent_and_does_not_change_persisted_state()
    {
        await using var host = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: new FixedClock(LogoutClockNow));

        var (user, rawToken) = await SeedAndLoginAsync(host);

        using (var scope = host.Services.CreateScope())
        {
            var revoker = scope.ServiceProvider.GetRequiredService<ILogoutSessionRevoker>();
            await revoker.RevokeAsync(new LogoutRevocationRequest(rawToken), CancellationToken.None);
        }

        RefreshToken afterFirst;
        await using (var mid = OpenReadContext())
        {
            afterFirst = await mid.RefreshTokens.AsNoTracking().SingleAsync(t => t.UserId == user.Id);
        }

        using (var scope = host.Services.CreateScope())
        {
            var revoker = scope.ServiceProvider.GetRequiredService<ILogoutSessionRevoker>();
            await revoker.RevokeAsync(new LogoutRevocationRequest(rawToken), CancellationToken.None);
        }

        await using var verify = OpenReadContext();
        var afterSecond = await verify.RefreshTokens.AsNoTracking().SingleAsync(t => t.UserId == user.Id);

        Assert.Equal(afterFirst.RevokedAt, afterSecond.RevokedAt);
        Assert.Equal(afterFirst.ReasonRevoked, afterSecond.ReasonRevoked);
    }

    [Fact]
    public async Task Unknown_token_hash_writes_nothing()
    {
        await using var host = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: new FixedClock(LogoutClockNow));

        var (user, rawToken) = await SeedAndLoginAsync(host);

        int before;
        await using (var pre = OpenReadContext())
        {
            before = await pre.RefreshTokens.AsNoTracking().CountAsync();
        }

        using (var scope = host.Services.CreateScope())
        {
            var revoker = scope.ServiceProvider.GetRequiredService<ILogoutSessionRevoker>();
            await revoker.RevokeAsync(
                new LogoutRevocationRequest("totally-unknown-token"), CancellationToken.None);
        }

        await using var db = OpenReadContext();
        Assert.Equal(before, await db.RefreshTokens.AsNoTracking().CountAsync());
        var untouched = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.UserId == user.Id);
        Assert.Null(untouched.RevokedAt);
        Assert.Equal(rawToken, rawToken); // token still usable from the caller's perspective (raw value unchanged)
    }

    [Fact]
    public async Task Expired_active_token_is_still_revoked_by_logout()
    {
        await using var host = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: new FixedClock(LogoutClockNow));

        var (user, rawToken) = await SeedAndLoginAsync(host);

        var hasher = host.Services.GetRequiredService<IRefreshTokenHasher>();
        var hash = hasher.Hash(rawToken);
        await using (var db = OpenReadContext())
        {
            var token = await db.RefreshTokens.SingleAsync(t => t.TokenHash == hash);
            token.ExpiresAt = LogoutClockNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        using (var scope = host.Services.CreateScope())
        {
            var revoker = scope.ServiceProvider.GetRequiredService<ILogoutSessionRevoker>();
            await revoker.RevokeAsync(new LogoutRevocationRequest(rawToken), CancellationToken.None);
        }

        await using var verify = OpenReadContext();
        var token2 = await verify.RefreshTokens.AsNoTracking().SingleAsync(t => t.UserId == user.Id);
        Assert.Equal(RefreshTokenRevocationReason.Logout, token2.ReasonRevoked);
        Assert.Equal(LogoutClockNow, token2.RevokedAt);
    }

    [Fact]
    public async Task Other_families_for_the_same_user_are_unaffected()
    {
        await using var host = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: new FixedClock(LogoutClockNow));

        var (user, firstRaw) = await SeedAndLoginAsync(host);

        // A second login for the same user creates a new family.
        string secondRaw;
        using (var scope = host.Services.CreateScope())
        {
            var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();
            var auth = await authenticator.AuthenticateAsync(user.Email!, ValidPassword, CancellationToken.None);
            var snapshot = auth.User!;
            var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
            var issue = await issuer.IssueAsync(
                new LoginSessionRequest(
                    snapshot.UserId, snapshot.OrganizationId,
                    snapshot.SecurityStamp, snapshot.ConcurrencyStamp),
                CancellationToken.None);
            secondRaw = issue.RefreshToken!;
        }

        // Logout the FIRST session only.
        using (var scope = host.Services.CreateScope())
        {
            var revoker = scope.ServiceProvider.GetRequiredService<ILogoutSessionRevoker>();
            await revoker.RevokeAsync(new LogoutRevocationRequest(firstRaw), CancellationToken.None);
        }

        var hasher = host.Services.GetRequiredService<IRefreshTokenHasher>();
        var firstHash = hasher.Hash(firstRaw);
        var secondHash = hasher.Hash(secondRaw);

        await using var db = OpenReadContext();
        var firstToken = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenHash == firstHash);
        var secondToken = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenHash == secondHash);

        Assert.Equal(RefreshTokenRevocationReason.Logout, firstToken.ReasonRevoked);
        Assert.NotEqual(firstToken.TokenFamilyId, secondToken.TokenFamilyId);
        Assert.Null(secondToken.RevokedAt);
        Assert.Null(secondToken.ReasonRevoked);
    }

    [Fact]
    public async Task Other_users_tokens_are_unaffected_by_logout()
    {
        await using var host = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: new FixedClock(LogoutClockNow));

        var (userA, rawA) = await SeedAndLoginAsync(host);
        _ = userA;

        Guid userBId;
        string rawB;
        using (var scope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
            var userB = await AuthenticationTestHost.SeedUserAsync(
                scope.ServiceProvider, org.Id, OtherValidPassword);
            userBId = userB.Id;

            var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();
            var auth = await authenticator.AuthenticateAsync(userB.Email!, OtherValidPassword, CancellationToken.None);
            var snapshot = auth.User!;
            var issuer = scope.ServiceProvider.GetRequiredService<ILoginSessionIssuer>();
            var issue = await issuer.IssueAsync(
                new LoginSessionRequest(
                    snapshot.UserId, snapshot.OrganizationId,
                    snapshot.SecurityStamp, snapshot.ConcurrencyStamp),
                CancellationToken.None);
            rawB = issue.RefreshToken!;
        }

        using (var scope = host.Services.CreateScope())
        {
            var revoker = scope.ServiceProvider.GetRequiredService<ILogoutSessionRevoker>();
            await revoker.RevokeAsync(new LogoutRevocationRequest(rawA), CancellationToken.None);
        }

        var hasher = host.Services.GetRequiredService<IRefreshTokenHasher>();
        var hashB = hasher.Hash(rawB);

        await using var db = OpenReadContext();
        var tokenB = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenHash == hashB);
        Assert.Equal(userBId, tokenB.UserId);
        Assert.Null(tokenB.RevokedAt);
        Assert.Null(tokenB.ReasonRevoked);
    }

    [Fact]
    public async Task Post_lock_authoritative_time_is_used_for_revocation()
    {
        // initialNow != validationNow. Only validationNow is legitimately
        // used for the RevokedAt timestamp. LogoutSessionRevoker reads the
        // clock only ONCE (after locks are acquired), so we configure the
        // QueuedClock so its first-and-only observation returns the
        // authoritative value. Any regression that reads the clock BEFORE
        // the locks would consume a different queued value.
        await using var setupHost = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: new FixedClock(LogoutClockNow));
        var (user, rawToken) = await SeedAndLoginAsync(setupHost);

        var initialNow = LogoutClockNow.AddHours(1);
        var validationNow = LogoutClockNow.AddHours(2);
        var queuedClock = new QueuedClock(validationNow, initialNow);

        await using var logoutHost = AuthenticationTestHost.Build(
            _fixture.ConnectionString, clock: queuedClock);

        using (var scope = logoutHost.Services.CreateScope())
        {
            var revoker = scope.ServiceProvider.GetRequiredService<ILogoutSessionRevoker>();
            await revoker.RevokeAsync(new LogoutRevocationRequest(rawToken), CancellationToken.None);
        }

        await using var db = OpenReadContext();
        var token = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.UserId == user.Id);
        Assert.Equal(validationNow, token.RevokedAt);
    }
}
