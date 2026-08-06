using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Authentication;
using OpsFlow.Infrastructure.Authentication;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Seeding;
using OpsFlow.Infrastructure.Time;

namespace OpsFlow.Infrastructure;

/// <summary>Registration of OpsFlow infrastructure services (persistence, Identity, seeding).</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers EF Core (SQL Server), ASP.NET Core Identity (Guid keys) and the
    /// development seeding infrastructure. The connection string is read from
    /// <c>ConnectionStrings:OpsFlow</c>.
    /// </summary>
    public static IServiceCollection AddOpsFlowInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("OpsFlow")
            ?? throw new InvalidOperationException(
                "Connection string 'ConnectionStrings:OpsFlow' is not configured. " +
                "Set it via user secrets or environment variables.");

        services.AddDbContext<OpsFlowDbContext>(options => options.UseSqlServer(connectionString));

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

        services.AddOptions<SeedOptions>().Bind(configuration.GetSection(SeedOptions.SectionName));

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<DevelopmentDataSeeder>();

        AddAuthenticationFoundation(services, configuration);

        return services;
    }

    private static void AddAuthenticationFoundation(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

        // All of these are stateless and safe as singletons.
        services.AddSingleton<JwtBearerTokenValidationParametersFactory>();
        services.AddSingleton<IAccessTokenService, JwtAccessTokenService>();
        services.AddSingleton<IRefreshTokenGenerator, RefreshTokenGenerator>();
        services.AddSingleton<IRefreshTokenHasher, RefreshTokenHasher>();

        // Login-time authentication adapters. The hash cache is a singleton so
        // the dummy hash is produced exactly once per process; the verifier and
        // authenticator are scoped because they depend on scoped Identity and
        // DbContext services. LoginService (Application) is registered from the
        // Api composition root, not here.
        services.AddSingleton<DummyPasswordHashCache>();
        services.AddScoped<IDummyPasswordVerifier, DummyPasswordVerifier>();
        services.AddScoped<IUserAuthenticator, IdentityUserAuthenticator>();
        services.AddScoped<ILoginSessionIssuer, LoginSessionIssuer>();
        services.AddScoped<IRefreshSessionRotator, RefreshSessionRotator>();
        services.AddScoped<ILogoutSessionRevoker, LogoutSessionRevoker>();
        services.AddScoped<ICurrentUserReader, CurrentUserReader>();
    }
}
