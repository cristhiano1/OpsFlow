using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Authentication;
using OpsFlow.Domain.Organizations;
using OpsFlow.Infrastructure.Authentication;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Time;

namespace OpsFlow.Api.IntegrationTests.Authentication;

/// <summary>
/// Builds an isolated ServiceProvider that mirrors the Infrastructure
/// authentication registrations against the shared Testcontainers SQL Server
/// fixture. It uses the same Identity password and lockout policy as
/// production so the exercised behavior matches the runtime configuration.
/// Seeding helpers require a caller-owned scope so scoped services are not
/// silently disposed while callers still hold references to them.
/// </summary>
internal sealed class AuthenticationTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private AuthenticationTestHost(ServiceProvider provider) => _provider = provider;

    public IServiceProvider Services => _provider;

    public static AuthenticationTestHost Build(
        string connectionString,
        IClock? clock = null,
        IRefreshTokenGenerator? tokenGenerator = null,
        IAccessTokenService? accessTokenService = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OpsFlowDbContext>(o => o.UseSqlServer(connectionString));

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<OpsFlowDbContext>();

        services.AddSingleton<DummyPasswordHashCache>();
        services.AddScoped<IDummyPasswordVerifier, DummyPasswordVerifier>();
        services.AddScoped<IUserAuthenticator, IdentityUserAuthenticator>();


        services.AddSingleton<IClock>(clock ?? new SystemClock());
        services.AddSingleton<IRefreshTokenGenerator>(tokenGenerator ?? new RefreshTokenGenerator());
        services.AddSingleton<IRefreshTokenHasher, RefreshTokenHasher>();
        services.Configure<JwtOptions>(o =>
        {
            o.Issuer = "OpsFlow.Test";
            o.Audience = "OpsFlow.Api.Test";
            o.SigningKey = Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            o.AccessTokenLifetimeMinutes = 15;
            o.RefreshTokenLifetimeDays = 7;
        });
        if (accessTokenService is null)
        {
            services.AddSingleton<IAccessTokenService, JwtAccessTokenService>();
        }
        else
        {
            services.AddSingleton(accessTokenService);
        }
        services.AddScoped<ILoginSessionIssuer, LoginSessionIssuer>();
        services.AddScoped<IRefreshSessionRotator, RefreshSessionRotator>();

        return new AuthenticationTestHost(services.BuildServiceProvider());
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();

    /// <summary>Seeds a new organization using the supplied caller-owned scope.</summary>
    public static async Task<Organization> SeedOrganizationAsync(
        IServiceProvider scopeServices, bool isActive = true)
    {
        var db = scopeServices.GetRequiredService<OpsFlowDbContext>();

        var org = new Organization
        {
            Id = Guid.NewGuid(),
            Name = "Test Organization",
            Slug = "org-" + Guid.NewGuid().ToString("N"),
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    /// <summary>Seeds a new user (and optionally assigns a role) using the supplied caller-owned scope.</summary>
    public static async Task<ApplicationUser> SeedUserAsync(
        IServiceProvider scopeServices,
        Guid organizationId,
        string password,
        bool isActive = true,
        string? role = null,
        string? email = null)
    {
        var users = scopeServices.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scopeServices.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        var actualEmail = email ?? ("u-" + Guid.NewGuid().ToString("N") + "@test.local");
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = actualEmail,
            Email = actualEmail,
            EmailConfirmed = true,
            DisplayName = "Test User",
            OrganizationId = organizationId,
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var createResult = await users.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to seed user: " + string.Join("; ", createResult.Errors.Select(e => e.Code)));
        }

        if (role is not null)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                var roleCreate = await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() });
                if (!roleCreate.Succeeded)
                {
                    throw new InvalidOperationException("Failed to seed role.");
                }
            }
            var addRole = await users.AddToRoleAsync(user, role);
            if (!addRole.Succeeded)
            {
                throw new InvalidOperationException("Failed to assign role.");
            }
        }

        return user;
    }
}
