using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.HealthChecks;

internal sealed class SqlServerHealthCheck(OpsFlowDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            bool canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy();
        }
        catch
        {
            return HealthCheckResult.Unhealthy();
        }
    }
}
