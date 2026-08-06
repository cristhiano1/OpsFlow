using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Application.Authentication;
using OpsFlow.Application.Authorization;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Authentication;

[Collection(SqlServerCollection.Name)]
public sealed class CurrentUserReaderTests
{
    private const string ValidPassword = "CorrectHorse!123456";

    private readonly SqlServerFixture _fixture;

    public CurrentUserReaderTests(SqlServerFixture fixture) => _fixture = fixture;

    private OpsFlowDbContext OpenReadContext() =>
        new(new DbContextOptionsBuilder<OpsFlowDbContext>()
            .UseSqlServer(_fixture.ConnectionString).Options);

    private static async Task<(ApplicationUser User, string SecurityStamp)> SeedActiveUserAsync(
        AuthenticationTestHost host,
        bool isActive = true,
        bool orgActive = true,
        string? role = OpsFlowRoles.Coordinator)
    {
        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider, orgActive);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, ValidPassword, isActive: isActive, role: role);
        return (user, user.SecurityStamp!);
    }

    // ================================================================
    // Success path
    // ================================================================

    [Fact]
    public async Task Read_returns_DB_authoritative_profile_when_all_checks_pass()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);
        var (user, stamp) = await SeedActiveUserAsync(host);

        using var scope = host.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ICurrentUserReader>();

        var result = await reader.ReadAsync(
            new CurrentUserQuery(user.Id, stamp), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.User);
        Assert.Equal(user.Id, result.User.UserId);
        Assert.Equal(user.Email, result.User.Email);
        Assert.Equal(user.DisplayName, result.User.DisplayName);
        Assert.Equal(user.OrganizationId, result.User.OrganizationId);
        Assert.NotNull(result.User.OrganizationName);
        Assert.Contains(OpsFlowRoles.Coordinator, result.User.Roles);
    }

    [Fact]
    public async Task Read_returns_current_DB_roles_not_stale_input()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);
        var (user, stamp) = await SeedActiveUserAsync(host, role: OpsFlowRoles.Viewer);

        // Add a second role AFTER the "issuance" moment.
        using (var scope = host.Services.CreateScope())
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

        using var readScope = host.Services.CreateScope();
        var reader = readScope.ServiceProvider.GetRequiredService<ICurrentUserReader>();

        // Note: adding a role via UserManager rotates ConcurrencyStamp but does
        // not rotate SecurityStamp, so the presented stamp still matches.
        var result = await reader.ReadAsync(
            new CurrentUserQuery(user.Id, stamp), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.User);
        Assert.Contains(OpsFlowRoles.Viewer, result.User.Roles);
        Assert.Contains(OpsFlowRoles.Technician, result.User.Roles);
    }

    // ================================================================
    // Failure paths
    // ================================================================

    [Fact]
    public async Task Read_returns_failure_when_user_id_is_empty()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);
        using var scope = host.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ICurrentUserReader>();

        var result = await reader.ReadAsync(
            new CurrentUserQuery(Guid.Empty, "any"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task Read_returns_failure_when_user_does_not_exist()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);
        using var scope = host.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ICurrentUserReader>();

        var result = await reader.ReadAsync(
            new CurrentUserQuery(Guid.NewGuid(), "any"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task Read_returns_failure_when_user_is_inactive()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);
        var (user, stamp) = await SeedActiveUserAsync(host, isActive: false);

        using var scope = host.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ICurrentUserReader>();

        var result = await reader.ReadAsync(
            new CurrentUserQuery(user.Id, stamp), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Read_returns_failure_when_user_is_locked_out()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);
        var (user, stamp) = await SeedActiveUserAsync(host);

        using (var scope = host.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var tracked = await userManager.FindByIdAsync(user.Id.ToString());
            _ = await userManager.SetLockoutEndDateAsync(
                tracked!, DateTimeOffset.UtcNow.AddHours(1));
        }

        using var readScope = host.Services.CreateScope();
        var reader = readScope.ServiceProvider.GetRequiredService<ICurrentUserReader>();

        var result = await reader.ReadAsync(
            new CurrentUserQuery(user.Id, stamp), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Read_returns_failure_when_security_stamp_does_not_match()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);
        var (user, _) = await SeedActiveUserAsync(host);

        using var scope = host.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ICurrentUserReader>();

        var result = await reader.ReadAsync(
            new CurrentUserQuery(user.Id, "not-the-current-stamp"), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Read_returns_failure_when_security_stamp_was_rotated()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);
        var (user, oldStamp) = await SeedActiveUserAsync(host);

        using (var scope = host.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var tracked = await userManager.FindByIdAsync(user.Id.ToString());
            _ = await userManager.UpdateSecurityStampAsync(tracked!);
        }

        using var readScope = host.Services.CreateScope();
        var reader = readScope.ServiceProvider.GetRequiredService<ICurrentUserReader>();

        var result = await reader.ReadAsync(
            new CurrentUserQuery(user.Id, oldStamp), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Read_returns_failure_when_current_security_stamp_is_blank()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);
        var (user, _) = await SeedActiveUserAsync(host);

        // Directly zero the SecurityStamp in the DB. A blank current stamp must
        // never be accepted as a valid session-binding anchor even when the
        // caller presents an equally blank value.
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            var tracked = await db.Users.SingleAsync(u => u.Id == user.Id);
            tracked.SecurityStamp = string.Empty;
            _ = await db.SaveChangesAsync();
        }

        using var readScope = host.Services.CreateScope();
        var reader = readScope.ServiceProvider.GetRequiredService<ICurrentUserReader>();

        // Present the same blank value the DB now has: the reader must still
        // fail because the blank current stamp is itself invalid.
        var result = await reader.ReadAsync(
            new CurrentUserQuery(user.Id, string.Empty), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Read_returns_failure_when_organization_is_inactive()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);
        var (user, stamp) = await SeedActiveUserAsync(host, orgActive: false);

        using var scope = host.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ICurrentUserReader>();

        var result = await reader.ReadAsync(
            new CurrentUserQuery(user.Id, stamp), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Read_returns_failure_when_organization_row_is_missing()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);
        var (user, stamp) = await SeedActiveUserAsync(host);

        // Bypass the User→Organization FK long enough to point the user at a
        // non-existent organization id. Restore the constraint at the end so
        // the shared fixture keeps its integrity for other tests.
        var orphanOrgId = Guid.NewGuid();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            _ = await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE dbo.AspNetUsers NOCHECK CONSTRAINT [FK_AspNetUsers_Organizations_OrganizationId];");
            try
            {
                _ = await db.Database.ExecuteSqlRawAsync(
                    "UPDATE dbo.AspNetUsers SET OrganizationId = {0} WHERE Id = {1};",
                    orphanOrgId, user.Id);
            }
            finally
            {
                _ = await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE dbo.AspNetUsers WITH NOCHECK CHECK CONSTRAINT [FK_AspNetUsers_Organizations_OrganizationId];");
            }
        }

        try
        {
            using var readScope = host.Services.CreateScope();
            var reader = readScope.ServiceProvider.GetRequiredService<ICurrentUserReader>();

            var result = await reader.ReadAsync(
                new CurrentUserQuery(user.Id, stamp), CancellationToken.None);

            Assert.False(result.Succeeded);
        }
        finally
        {
            // Best-effort cleanup so the orphan user is not left in the shared
            // fixture; use raw SQL to avoid the concurrency-stamp round-trip.
            await using var db = OpenReadContext();
            _ = await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM dbo.AspNetUserRoles WHERE UserId = {0}; " +
                "DELETE FROM dbo.RefreshTokens WHERE UserId = {0}; " +
                "DELETE FROM dbo.AspNetUsers WHERE Id = {0};",
                user.Id);
        }
    }
}
