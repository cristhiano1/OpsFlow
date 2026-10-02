using System.Globalization;
using System.Threading.RateLimiting;
using OpsFlow.Application.Authorization;

namespace OpsFlow.Api.RateLimiting;

internal static class RateLimitingServiceCollectionExtensions
{
    public static IServiceCollection AddOpsFlowRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };

            options.AddPolicy(RateLimitPolicies.AuthStrict, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: GetIpAddress(context),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = configuration.GetValue("RateLimiting:Auth:PermitLimit", 10),
                        Window = TimeSpan.FromSeconds(
                            configuration.GetValue("RateLimiting:Auth:WindowSeconds", 60)),
                        QueueLimit = 0,
                    }));

            options.AddPolicy(RateLimitPolicies.ApiStandard, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: GetPartitionKey(context),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = configuration.GetValue("RateLimiting:Api:PermitLimit", 60),
                        Window = TimeSpan.FromSeconds(
                            configuration.GetValue("RateLimiting:Api:WindowSeconds", 60)),
                        QueueLimit = 0,
                    }));

            options.AddPolicy(RateLimitPolicies.RagExpensive, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: GetPartitionKey(context),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = configuration.GetValue("RateLimiting:Rag:PermitLimit", 10),
                        Window = TimeSpan.FromSeconds(
                            configuration.GetValue("RateLimiting:Rag:WindowSeconds", 60)),
                        QueueLimit = 0,
                    }));

            options.AddPolicy(RateLimitPolicies.Upload, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: GetPartitionKey(context),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = configuration.GetValue("RateLimiting:Upload:PermitLimit", 10),
                        Window = TimeSpan.FromSeconds(
                            configuration.GetValue("RateLimiting:Upload:WindowSeconds", 60)),
                        QueueLimit = 0,
                    }));
        });

        return services;
    }

    private static string GetPartitionKey(HttpContext context)
    {
        var userId = context.User.FindFirst(OpsFlowClaimTypes.Subject)?.Value;
        return !string.IsNullOrEmpty(userId)
            ? $"user:{userId}"
            : $"ip:{GetIpAddress(context)}";
    }

    private static string GetIpAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
