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

    private async Task<int> CountSeedUsersAsync()
    {
        await using var db = _fixture.CreateContext();
        return await db.Users.CountAsync(u =>
            u.Email == "admin@opsflow.local" || u.Email == "admin@contoso.local");
    }

    private static async Task RunSeederAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DevelopmentDataSeeder>();
        await seeder.SeedAsync();
    }

    private ServiceProvider BuildProvider(string? defaultPassword)
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
        }));
        services.AddScoped<DevelopmentDataSeeder>();

        return services.BuildServiceProvider();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
