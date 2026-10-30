using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Seeding;
using OpsFlow.Infrastructure.UnitTests.TestSupport;

namespace OpsFlow.Infrastructure.UnitTests.Seeding;

// Upgrade paths for existing databases, on EF InMemory with real ASP.NET Identity.
// SeedingTests (integration) covers the same flow against SQL Server.
public sealed class DevelopmentDataSeederTests
{
    private const string ViewerEmail = "viewer@opsflow.local";

    private static readonly string[] PrivilegedEmails =
    [
        "admin@opsflow.local",
        "coordinator@opsflow.local",
        "technician@opsflow.local",
        "admin@contoso.local",
    ];

    private readonly string _databaseName = "seeder-" + Guid.NewGuid();

    private static string NewPassword() => "Aa1!" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Existing_viewer_on_shared_password_moves_to_newly_configured_viewer_password()
    {
        var privileged = NewPassword();
        var viewer = NewPassword();
        await SeedAsync(privileged, viewerPassword: null);
        Assert.True(await AcceptsAsync(ViewerEmail, privileged));

        await SeedAsync(privileged, viewer);

        Assert.True(await AcceptsAsync(ViewerEmail, viewer));
        Assert.False(await AcceptsAsync(ViewerEmail, privileged));
        foreach (var email in PrivilegedEmails)
        {
            Assert.True(await AcceptsAsync(email, privileged));
        }
    }

    [Fact]
    public async Task Updating_a_password_rotates_the_security_stamp()
    {
        var privileged = NewPassword();
        await SeedAsync(privileged, viewerPassword: null);
        var before = await SecurityStampAsync(ViewerEmail);

        await SeedAsync(privileged, NewPassword());

        Assert.NotEqual(before, await SecurityStampAsync(ViewerEmail));
    }

    [Fact]
    public async Task Reseeding_with_unchanged_passwords_leaves_accounts_untouched()
    {
        var privileged = NewPassword();
        var viewer = NewPassword();
        await SeedAsync(privileged, viewer);
        var viewerStamp = await SecurityStampAsync(ViewerEmail);
        var adminStamp = await SecurityStampAsync(PrivilegedEmails[0]);

        await SeedAsync(privileged, viewer);

        Assert.Equal(viewerStamp, await SecurityStampAsync(ViewerEmail));
        Assert.Equal(adminStamp, await SecurityStampAsync(PrivilegedEmails[0]));
    }

    [Fact]
    public async Task PublicDemo_refuses_when_existing_viewer_still_accepts_the_default_password()
    {
        var legacyShared = NewPassword();
        var viewer = NewPassword();
        await SeedAsync(legacyShared, viewerPassword: null);
        var stampBefore = await SecurityStampAsync(ViewerEmail);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SeedAsync(legacyShared, viewer, SeedMode.PublicDemo));

        Assert.Contains("Choose a new Seed:DefaultPassword", ex.Message);
        Assert.DoesNotContain(legacyShared, ex.Message);
        Assert.DoesNotContain(viewer, ex.Message);
        Assert.True(await AcceptsAsync(ViewerEmail, legacyShared));
        Assert.Equal(stampBefore, await SecurityStampAsync(ViewerEmail));
    }

    [Fact]
    public async Task PublicDemo_with_rotated_default_password_retires_the_old_shared_credential()
    {
        var legacyShared = NewPassword();
        var privileged = NewPassword();
        var viewer = NewPassword();
        await SeedAsync(legacyShared, viewerPassword: null);

        await SeedAsync(privileged, viewer, SeedMode.PublicDemo);

        Assert.True(await AcceptsAsync(ViewerEmail, viewer));
        Assert.False(await AcceptsAsync(ViewerEmail, legacyShared));
        foreach (var email in PrivilegedEmails)
        {
            Assert.True(await AcceptsAsync(email, privileged));
            Assert.False(await AcceptsAsync(email, legacyShared));
            Assert.False(await AcceptsAsync(email, viewer));
        }
    }

    [Fact]
    public async Task Rotating_the_default_password_updates_existing_privileged_accounts()
    {
        var first = NewPassword();
        var second = NewPassword();
        var viewer = NewPassword();
        await SeedAsync(first, viewer, SeedMode.PublicDemo);

        await SeedAsync(second, viewer, SeedMode.PublicDemo);

        foreach (var email in PrivilegedEmails)
        {
            Assert.True(await AcceptsAsync(email, second));
            Assert.False(await AcceptsAsync(email, first));
        }

        Assert.True(await AcceptsAsync(ViewerEmail, viewer));
    }

    [Fact]
    public async Task Configured_password_that_fails_policy_does_not_change_the_existing_account()
    {
        var privileged = NewPassword();
        await SeedAsync(privileged, viewerPassword: null);
        const string weak = "weakpassword";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(weak, viewerPassword: null));

        Assert.DoesNotContain(weak, ex.Message);
        Assert.True(await AcceptsAsync(PrivilegedEmails[0], privileged));
    }

    private async Task SeedAsync(string? defaultPassword, string? viewerPassword, SeedMode mode = SeedMode.Development)
    {
        await using var provider = BuildProvider(defaultPassword, viewerPassword);
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DevelopmentDataSeeder>().SeedAsync(mode);
    }

    private async Task<bool> AcceptsAsync(string email, string password)
    {
        await using var provider = BuildProvider(defaultPassword: null, viewerPassword: null);
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        return await users.CheckPasswordAsync(user, password);
    }

    private async Task<string?> SecurityStampAsync(string email)
    {
        await using var provider = BuildProvider(defaultPassword: null, viewerPassword: null);
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        return user.SecurityStamp;
    }

    private ServiceProvider BuildProvider(string? defaultPassword, string? viewerPassword)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OpsFlowDbContext>(options => options.UseInMemoryDatabase(_databaseName));

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<OpsFlowDbContext>();

        services.AddSingleton<IClock>(new FixedClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));
        services.AddSingleton(Options.Create(new SeedOptions
        {
            Enabled = true,
            DefaultPassword = defaultPassword,
            ViewerPassword = viewerPassword,
        }));
        services.AddScoped<DevelopmentDataSeeder>();

        return services.BuildServiceProvider();
    }
}
