using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpsFlow.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by the EF Core tools (for example
/// <c>dotnet ef migrations add</c> / <c>remove</c> / <c>database update</c>).
/// It only constructs <see cref="OpsFlowDbContext"/>; it never migrates, seeds
/// or starts the API host. The connection string is read exclusively from the
/// <c>OPSFLOW_DESIGNTIME_CONNECTION_STRING</c> environment variable and is never
/// hardcoded. Runtime configuration is unaffected and keeps using ASP.NET Core
/// configuration / user secrets.
/// </summary>
internal sealed class OpsFlowDbContextFactory : IDesignTimeDbContextFactory<OpsFlowDbContext>
{
    private const string ConnectionStringVariable = "OPSFLOW_DESIGNTIME_CONNECTION_STRING";

    public OpsFlowDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Design-time EF Core operations require the '{ConnectionStringVariable}' environment variable.");
        }

        var options = new DbContextOptionsBuilder<OpsFlowDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new OpsFlowDbContext(options);
    }
}
