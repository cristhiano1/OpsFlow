namespace OpsFlow.Api.Cors;

internal static class CorsFlowExtensions
{
    public const string PolicyName = "OpsFlowCors";

    public static IServiceCollection AddOpsFlowCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var allowedOrigins = configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? [];

        services.AddCors(options =>
        {
            options.AddPolicy(PolicyName, policy =>
            {
                if (allowedOrigins.Length > 0)
                {
                    policy.WithOrigins(allowedOrigins)
                        .WithMethods("GET", "POST")
                        .WithHeaders("Content-Type", "Authorization")
                        .AllowCredentials();
                }
            });
        });

        return services;
    }
}
