using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace OpsFlow.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Starts a disposable SQL Server 2025 container with Full-Text Search enabled
/// (Testcontainers), creates a user database, applies the real EF Core
/// migrations once, and hands out contexts pointing at it.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string DatabaseName = "OpsFlowTest";

    private static readonly IFutureDockerImage FtsImage = new ImageFromDockerfileBuilder()
        .WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), "docker/sqlserver-fts")
        .WithDockerfile("Dockerfile")
        .Build();

    private MsSqlContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await FtsImage.CreateAsync();

        _container = new MsSqlBuilder(FtsImage).Build();
        await _container.StartAsync();

        var masterConnectionString = _container.GetConnectionString();

        await using (var masterConn = new SqlConnection(masterConnectionString))
        {
            await masterConn.OpenAsync();
            await using var cmd = masterConn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{DatabaseName}];";
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = DatabaseName
        };
        ConnectionString = builder.ConnectionString;

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

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
