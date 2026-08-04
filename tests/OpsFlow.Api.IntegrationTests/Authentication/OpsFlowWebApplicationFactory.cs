using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace OpsFlow.Api.IntegrationTests.Authentication;

internal sealed class OpsFlowWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string ConnectionStringEnvironmentVariable = "ConnectionStrings__OpsFlow";

    private readonly string? _previousConnectionString;

    public OpsFlowWebApplicationFactory(string connectionString)
    {
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

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            });
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
