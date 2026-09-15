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
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Documents;
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

        AddEmbeddingProvider(services, configuration);
        AddAnswerGenerationProvider(services, configuration);
        AddRerankingProvider(services, configuration);
        AddAuthenticationFoundation(services, configuration);

        return services;
    }

    // Internal so the infrastructure unit tests can exercise it without
    // requiring a full DbContext + JWT composition.
    internal static void AddEmbeddingProvider(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpenAIEmbeddingOptions>()
            .Bind(configuration.GetSection(OpenAIEmbeddingOptions.SectionName));
        services.AddSingleton<IEmbeddingGenerator, OpenAIEmbeddingGenerator>();
    }

    // Internal so the infrastructure unit tests can exercise it without
    // requiring a full DbContext + JWT composition. Binds the shared "OpenAI"
    // section (reusing the embedding provider's ApiKey plus AnswerModel).
    internal static void AddAnswerGenerationProvider(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpenAIAnswerGenerationOptions>()
            .Bind(configuration.GetSection(OpenAIAnswerGenerationOptions.SectionName));
        services.AddSingleton<IGroundedAnswerGenerator, OpenAiGroundedAnswerGenerator>();
    }

    // Internal so the infrastructure unit tests can exercise it without
    // requiring a full DbContext + JWT composition. Registers the production
    // Amazon Bedrock reranker behind the provider-neutral IChunkReranker port.
    // It intentionally does NOT register SearchDocumentChunksRerankedService:
    // reranking is not activated in any user-facing path in this PR.
    internal static void AddRerankingProvider(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BedrockRerankerOptions>()
            .Bind(configuration.GetSection(BedrockRerankerOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<BedrockRerankerOptions>, BedrockRerankerOptionsValidator>();

        // Stateless adapters over a thread-safe AWS client; safe as singletons.
        // The invoker owns and disposes the AWS client for the process lifetime.
        services.AddSingleton<IBedrockRerankInvoker, BedrockRerankInvoker>();
        services.AddSingleton<IChunkReranker, BedrockChunkReranker>();
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
