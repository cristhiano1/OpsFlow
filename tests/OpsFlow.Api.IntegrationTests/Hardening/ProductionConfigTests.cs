using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpsFlow.Api.IntegrationTests.Authentication;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Seeding;

namespace OpsFlow.Api.IntegrationTests.Hardening;

[Collection(SqlServerCollection.Name)]
public sealed class ProductionConfigTests : IDisposable
{
    private readonly OpsFlowWebApplicationFactory _factory;

    public ProductionConfigTests(SqlServerFixture fixture)
    {
        _factory = new OpsFlowWebApplicationFactory(fixture.ConnectionString);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void Seed_options_are_bound_from_configuration()
    {
        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<SeedOptions>>();

        Assert.NotNull(options.Value);
    }

    [Fact]
    public void DevelopmentDataSeeder_is_registered()
    {
        using var scope = _factory.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DevelopmentDataSeeder>();

        Assert.NotNull(seeder);
    }
}
