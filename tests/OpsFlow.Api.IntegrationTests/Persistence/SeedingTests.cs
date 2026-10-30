using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Authorization;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Seeding;

namespace OpsFlow.Api.IntegrationTests.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class SeedingTests
{
    private const string ViewerEmail = "viewer@opsflow.local";

    private readonly SqlServerFixture _fixture;

    public SeedingTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Seeding_is_idempotent_and_creates_the_expected_data()
    {
        // A policy-compliant, throwaway password generated at runtime (never committed).
        var password = "Aa1!" + Guid.NewGuid().ToString("N");
        await using var provider = BuildProvider(password);

        await RunSeederAsync(provider);
        await RunSeederAsync(provider); // second run must not duplicate or throw

        await using var db = _fixture.CreateContext();

        Assert.Equal(1, await db.Organizations.CountAsync(o => o.Slug == "northwind-field-services"));
        Assert.Equal(1, await db.Organizations.CountAsync(o => o.Slug == "contoso-operations"));

        foreach (var role in OpsFlowRoles.All)
        {
            Assert.Equal(1, await db.Roles.CountAsync(r => r.Name == role));
        }

        Assert.Equal(1, await db.Users.CountAsync(u => u.Email == "admin@opsflow.local"));
        Assert.Equal(1, await db.Users.CountAsync(u => u.Email == "admin@contoso.local"));
    }

    [Fact]
    public async Task Seeding_without_a_password_seeds_roles_and_organizations_but_no_users()
    {
        await using var provider = BuildProvider(defaultPassword: null);

        var beforeUsers = await CountSeedUsersAsync();
        await RunSeederAsync(provider);
        var afterUsers = await CountSeedUsersAsync();

        await using var db = _fixture.CreateContext();
        Assert.Equal(4, await db.Roles.CountAsync(r => OpsFlowRoles.All.Contains(r.Name!)));
        Assert.Equal(beforeUsers, afterUsers);
    }

    [Fact]
    public async Task Seeding_with_same_viewer_and_default_password_throws_configuration_error()
    {
        var sharedPassword = "Aa1!" + Guid.NewGuid().ToString("N");
        await using var provider = BuildProvider(sharedPassword, viewerPassword: sharedPassword);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunSeederAsync(provider));
        Assert.Contains("ViewerPassword", ex.Message);
        Assert.Contains("DefaultPassword", ex.Message);
        Assert.DoesNotContain(sharedPassword, ex.Message);
    }

    [Theory]
    [InlineData(SeedMode.Development)]
    [InlineData(SeedMode.PublicDemo)]
    public async Task Seeding_with_viewer_password_uses_separate_credential_for_viewer_account(SeedMode mode)
    {
        var defaultPassword = "Aa1!" + Guid.NewGuid().ToString("N");
        var viewerPassword = "Vv2!" + Guid.NewGuid().ToString("N");
        await using var provider = BuildProvider(defaultPassword, viewerPassword);
        await DeleteViewerAsync(provider);

        await RunSeederAsync(provider, mode);

        Assert.True(await ViewerHasPasswordAsync(provider, viewerPassword));
        Assert.False(await ViewerHasPasswordAsync(provider, defaultPassword));
    }

    // Docker Compose passes an unset SEED_VIEWER_PASSWORD through as "".
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Development_seeding_with_blank_viewer_password_falls_back_to_default(string viewerPassword)
    {
        var defaultPassword = "Aa1!" + Guid.NewGuid().ToString("N");
        await using var provider = BuildProvider(defaultPassword, viewerPassword);
        await DeleteViewerAsync(provider);

        await RunSeederAsync(provider);

        Assert.True(await ViewerHasPasswordAsync(provider, defaultPassword));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Public_demo_seeding_without_viewer_password_fails_before_creating_users(string? viewerPassword)
    {
        var defaultPassword = "Aa1!" + Guid.NewGuid().ToString("N");
        await using var provider = BuildProvider(defaultPassword, viewerPassword);
        await DeleteViewerAsync(provider);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunSeederAsync(provider, SeedMode.PublicDemo));

        Assert.Contains("Seed:ViewerPassword is required", ex.Message);
        Assert.DoesNotContain(defaultPassword, ex.Message);
        Assert.False(await ViewerExistsAsync(provider));
    }

    private async Task<int> CountSeedUsersAsync()
    {
        await using var db = _fixture.CreateContext();
        return await db.Users.CountAsync(u =>
            u.Email == "admin@opsflow.local" || u.Email == "admin@contoso.local");
    }

    private static async Task RunSeederAsync(ServiceProvider provider, SeedMode mode = SeedMode.Development)
    {
        using var scope = provider.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DevelopmentDataSeeder>();
        await seeder.SeedAsync(mode);
    }

    // Seeding is idempotent and the database is shared across the collection, so an
    // existing viewer would keep whichever password an earlier test gave it.
    private static async Task DeleteViewerAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var viewer = await users.FindByEmailAsync(ViewerEmail);
        if (viewer is not null)
        {
            Assert.True((await users.DeleteAsync(viewer)).Succeeded);
        }
    }

    private static async Task<bool> ViewerExistsAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await users.FindByEmailAsync(ViewerEmail) is not null;
    }

    private static async Task<bool> ViewerHasPasswordAsync(ServiceProvider provider, string password)
    {
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var viewer = await users.FindByEmailAsync(ViewerEmail);
        Assert.NotNull(viewer);
        return await users.CheckPasswordAsync(viewer, password);
    }

    private ServiceProvider BuildProvider(string? defaultPassword, string? viewerPassword = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OpsFlowDbContext>(options => options.UseSqlServer(_fixture.ConnectionString));

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

        services.AddSingleton<IClock>(new FixedClock());
        services.AddSingleton(Options.Create(new SeedOptions
        {
            Enabled = true,
            DefaultPassword = defaultPassword,
            ViewerPassword = viewerPassword,
        }));
        services.AddScoped<DevelopmentDataSeeder>();

        return services.BuildServiceProvider();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
