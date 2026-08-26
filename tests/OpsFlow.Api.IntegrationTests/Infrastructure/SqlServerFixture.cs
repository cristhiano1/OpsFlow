using Microsoft.EntityFrameworkCore;
using OpsFlow.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace OpsFlow.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Starts a disposable SQL Server 2025 container (Testcontainers), applies the
/// real EF Core migrations once, and hands out contexts pointing at it.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest").Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public OpsFlowDbContext CreateContext()
        => new(new DbContextOptionsBuilder<OpsFlowDbContext>()
            .UseSqlServer(ConnectionString)
            .Options);
}

[CollectionDefinition(SqlServerCollection.Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql-server";
}
