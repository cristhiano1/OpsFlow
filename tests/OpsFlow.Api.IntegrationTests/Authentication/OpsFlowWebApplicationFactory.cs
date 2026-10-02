using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace OpsFlow.Api.IntegrationTests.Authentication;

internal sealed class OpsFlowWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string ConnectionStringEnvironmentVariable = "ConnectionStrings__OpsFlow";

    private readonly string? _previousConnectionString;
    private readonly Dictionary<string, string?>? _additionalConfig;

    public OpsFlowWebApplicationFactory(
        string connectionString,
        Dictionary<string, string?>? additionalConfig = null)
    {
        _additionalConfig = additionalConfig;

        _previousConnectionString = Environment.GetEnvironmentVariable(
            ConnectionStringEnvironmentVariable,
            EnvironmentVariableTarget.Process);

        Environment.SetEnvironmentVariable(
            ConnectionStringEnvironmentVariable,
            connectionString,
            EnvironmentVariableTarget.Process);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        var config = new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["RateLimiting:Auth:PermitLimit"] = "10000",
            ["RateLimiting:Api:PermitLimit"] = "10000",
            ["RateLimiting:Rag:PermitLimit"] = "10000",
            ["RateLimiting:Upload:PermitLimit"] = "10000",
            ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
        };

        if (_additionalConfig is not null)
        {
            foreach (var (key, value) in _additionalConfig)
            {
                config[key] = value;
            }
        }

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(config);
        });
    }

    protected override void Dispose(bool disposing)
    {
        RestoreConnectionStringEnvironmentVariable();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        RestoreConnectionStringEnvironmentVariable();
        await base.DisposeAsync();
    }

    private void RestoreConnectionStringEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(
            ConnectionStringEnvironmentVariable,
            _previousConnectionString,
            EnvironmentVariableTarget.Process);
    }
}
