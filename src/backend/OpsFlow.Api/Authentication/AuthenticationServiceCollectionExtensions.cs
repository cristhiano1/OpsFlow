using Microsoft.AspNetCore.Authentication.JwtBearer;
using OpsFlow.Infrastructure.Authentication;

namespace OpsFlow.Api.Authentication;

/// <summary>Registers JWT bearer authentication and authorization for the OpsFlow API.</summary>
public static class AuthenticationServiceCollectionExtensions
{
    /// <summary>
    /// Adds JWT bearer authentication as the default scheme, using the validated
    /// <c>JwtOptions</c> and the shared
    /// <see cref="JwtBearerTokenValidationParametersFactory"/> from the
    /// infrastructure layer. Database-backed session validation (active user,
    /// active organization, security-stamp equality) is added in a later
    /// sub-checkpoint when <c>IUserSessionValidator</c> exists.
    /// </summary>
    public static IServiceCollection AddOpsFlowAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwtBearerTokenValidationParametersFactory>((bearer, parametersFactory) =>
            {
                bearer.MapInboundClaims = false;
                bearer.SaveToken = false;
                bearer.TokenValidationParameters = parametersFactory.Create();
            });

        services.AddAuthorization();

        return services;
    }
}
