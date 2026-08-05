using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Application.Authentication;
using OpsFlow.Application.Authorization;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Authentication;

[Collection(SqlServerCollection.Name)]
public sealed class IdentityUserAuthenticatorTests
{
    private const string ValidPassword = "CorrectHorse!123456";
    private const string WrongPassword = "WrongHorse!123456";

    private readonly SqlServerFixture _fixture;

    public IdentityUserAuthenticatorTests(SqlServerFixture fixture) => _fixture = fixture;

    private OpsFlowDbContext OpenReadContext() =>
        new(new DbContextOptionsBuilder<OpsFlowDbContext>().UseSqlServer(_fixture.ConnectionString).Options);

    [Fact]
    public async Task Valid_credentials_return_success()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);

        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, ValidPassword, role: OpsFlowRoles.Viewer);
        var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();

        var result = await authenticator.AuthenticateAsync(user.Email!, ValidPassword, CancellationToken.None);

        Assert.Equal(AuthenticationStatus.Success, result.Status);
        Assert.NotNull(result.User);
        Assert.Equal(user.Id, result.User!.UserId);
        Assert.Equal(user.Email, result.User.Email);
        Assert.Equal(org.Id, result.User.OrganizationId);
        Assert.Equal(org.Name, result.User.OrganizationName);
        Assert.Contains(OpsFlowRoles.Viewer, result.User.Roles);
        Assert.False(string.IsNullOrWhiteSpace(result.User.SecurityStamp));
    }

    [Fact]
    public async Task Unknown_email_returns_invalid_credentials()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);

        using var scope = host.Services.CreateScope();
        var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();

        var result = await authenticator.AuthenticateAsync(
            "unknown-" + Guid.NewGuid().ToString("N") + "@test.local",
            ValidPassword,
            CancellationToken.None);

        Assert.Equal(AuthenticationStatus.InvalidCredentials, result.Status);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task Wrong_password_returns_invalid_credentials()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);

        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(scope.ServiceProvider, org.Id, ValidPassword);
        var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();

        var result = await authenticator.AuthenticateAsync(user.Email!, WrongPassword, CancellationToken.None);

        Assert.Equal(AuthenticationStatus.InvalidCredentials, result.Status);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task Wrong_password_increments_the_access_failed_count()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);

        Guid userId;
        using (var scope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
            var user = await AuthenticationTestHost.SeedUserAsync(scope.ServiceProvider, org.Id, ValidPassword);
            userId = user.Id;
            var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();

            _ = await authenticator.AuthenticateAsync(user.Email!, WrongPassword, CancellationToken.None);
        }

        await using var db = OpenReadContext();
        var reread = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal(1, reread.AccessFailedCount);
    }

    [Fact]
    public async Task Reaching_the_lockout_threshold_locks_the_user_out()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);

        Guid userId;
        using (var scope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
            var user = await AuthenticationTestHost.SeedUserAsync(scope.ServiceProvider, org.Id, ValidPassword);
            userId = user.Id;
            var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();

            for (var i = 0; i < 5; i++)
            {
                _ = await authenticator.AuthenticateAsync(user.Email!, WrongPassword, CancellationToken.None);
            }
        }

        await using var db = OpenReadContext();
        var reread = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.NotNull(reread.LockoutEnd);
        Assert.True(reread.LockoutEnd > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Already_locked_out_user_returns_locked_out_without_verifying_the_real_password_or_mutating_identity()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);

        Guid userId;
        string userEmail;
        var initialLockoutEnd = DateTimeOffset.UtcNow.AddMinutes(30);

        using (var seedScope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(seedScope.ServiceProvider);
            var user = await AuthenticationTestHost.SeedUserAsync(seedScope.ServiceProvider, org.Id, ValidPassword);
            userId = user.Id;
            userEmail = user.Email!;
        }

        // Force the user into a locked state via a direct DB update.
        await using (var db = OpenReadContext())
        {
            var tracked = await db.Users.SingleAsync(u => u.Id == userId);
            tracked.LockoutEnd = initialLockoutEnd;
            tracked.AccessFailedCount = 5;
            await db.SaveChangesAsync();
        }

        AuthenticationResult result;
        using (var scope = host.Services.CreateScope())
        {
            var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();
            // Even with the CORRECT password, a locked user must return LockedOut.
            result = await authenticator.AuthenticateAsync(userEmail, ValidPassword, CancellationToken.None);
        }

        Assert.Equal(AuthenticationStatus.LockedOut, result.Status);

        await using var verify = OpenReadContext();
        var reread = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal(5, reread.AccessFailedCount);
        Assert.Equal(initialLockoutEnd.ToUnixTimeSeconds(), reread.LockoutEnd!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Inactive_user_returns_user_inactive()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);

        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, ValidPassword, isActive: false);
        var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();

        var result = await authenticator.AuthenticateAsync(user.Email!, ValidPassword, CancellationToken.None);

        Assert.Equal(AuthenticationStatus.UserInactive, result.Status);
    }

    [Fact]
    public async Task Inactive_organization_returns_organization_inactive()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);

        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider, isActive: false);
        var user = await AuthenticationTestHost.SeedUserAsync(scope.ServiceProvider, org.Id, ValidPassword);
        var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();

        var result = await authenticator.AuthenticateAsync(user.Email!, ValidPassword, CancellationToken.None);

        Assert.Equal(AuthenticationStatus.OrganizationInactive, result.Status);
    }

    [Fact]
    public async Task Success_includes_the_current_roles()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);

        using var scope = host.Services.CreateScope();
        var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
        var user = await AuthenticationTestHost.SeedUserAsync(
            scope.ServiceProvider, org.Id, ValidPassword, role: OpsFlowRoles.Coordinator);
        var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();

        var result = await authenticator.AuthenticateAsync(user.Email!, ValidPassword, CancellationToken.None);

        Assert.Equal(AuthenticationStatus.Success, result.Status);
        Assert.Contains(OpsFlowRoles.Coordinator, result.User!.Roles);
    }

    [Fact]
    public async Task Success_includes_the_current_security_and_concurrency_stamps()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);

        Guid userId;
        string returnedSecurityStamp;
        string returnedConcurrencyStamp;
        using (var scope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
            var user = await AuthenticationTestHost.SeedUserAsync(scope.ServiceProvider, org.Id, ValidPassword);
            userId = user.Id;
            var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();

            var result = await authenticator.AuthenticateAsync(user.Email!, ValidPassword, CancellationToken.None);
            Assert.Equal(AuthenticationStatus.Success, result.Status);
            Assert.False(string.IsNullOrWhiteSpace(result.User!.SecurityStamp));
            Assert.False(string.IsNullOrWhiteSpace(result.User.ConcurrencyStamp));
            returnedSecurityStamp = result.User.SecurityStamp;
            returnedConcurrencyStamp = result.User.ConcurrencyStamp;
        }

        await using var db = OpenReadContext();
        var reread = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal(reread.SecurityStamp, returnedSecurityStamp);
        Assert.Equal(reread.ConcurrencyStamp, returnedConcurrencyStamp, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Successful_authentication_does_not_reset_the_access_failed_count()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);

        Guid userId;
        using (var scope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
            var user = await AuthenticationTestHost.SeedUserAsync(scope.ServiceProvider, org.Id, ValidPassword);
            userId = user.Id;
            var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();

            _ = await authenticator.AuthenticateAsync(user.Email!, WrongPassword, CancellationToken.None);
            _ = await authenticator.AuthenticateAsync(user.Email!, WrongPassword, CancellationToken.None);

            var success = await authenticator.AuthenticateAsync(user.Email!, ValidPassword, CancellationToken.None);
            Assert.Equal(AuthenticationStatus.Success, success.Status);
        }

        await using var db = OpenReadContext();
        var reread = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal(2, reread.AccessFailedCount);
    }

    [Fact]
    public async Task Successful_authentication_does_not_update_last_login_at()
    {
        await using var host = AuthenticationTestHost.Build(_fixture.ConnectionString);

        Guid userId;
        using (var scope = host.Services.CreateScope())
        {
            var org = await AuthenticationTestHost.SeedOrganizationAsync(scope.ServiceProvider);
            var user = await AuthenticationTestHost.SeedUserAsync(scope.ServiceProvider, org.Id, ValidPassword);
            userId = user.Id;
            var authenticator = scope.ServiceProvider.GetRequiredService<IUserAuthenticator>();

            var result = await authenticator.AuthenticateAsync(user.Email!, ValidPassword, CancellationToken.None);
            Assert.Equal(AuthenticationStatus.Success, result.Status);
        }

        await using var db = OpenReadContext();
        var reread = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Null(reread.LastLoginAt);
    }
}
