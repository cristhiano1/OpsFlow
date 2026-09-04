using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Documents;

/// <summary>
/// Verifies the full-text search migration reaches a fully-applied state after
/// retrying a partially-applied nontransactional execution — the scenario where
/// the FTS catalog/index were created but <c>__EFMigrationsHistory</c> was not
/// updated before the deployment was interrupted.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class FullTextSearchMigrationIntegrationTests
{
    private const string FtsMigrationId = "20260902182613_AddFullTextSearchOnDocumentChunks";

    private readonly SqlServerFixture _fixture;

    public FullTextSearchMigrationIntegrationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FtsMigration_reapplies_cleanly_after_partial_nontransactional_execution()
    {
        var dbName = "OpsFlowFtsRetry_" + Guid.NewGuid().ToString("N")[..8];
        var masterConnectionString = WithCatalog("master");
        var dbConnectionString = WithCatalog(dbName);

        try
        {
            await ExecuteNonQueryAsync(masterConnectionString, $"CREATE DATABASE [{dbName}];");

            // A. Migrate fresh — creates every table plus FTS catalog and index.
            await using (var context = CreateContext(dbConnectionString))
            {
                await context.Database.MigrateAsync();
            }

            // B. Verify fresh migration recorded the FTS history row.
            var historyAfterFresh = await ScalarAsync(dbConnectionString,
                $"SELECT COUNT(*) FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'{FtsMigrationId}';");
            Assert.Equal(1, historyAfterFresh);

            // C. Simulate interrupted deployment: FTS objects exist, history row lost.
            var rowsDeleted = await ExecuteNonQueryAsync(dbConnectionString,
                $"DELETE FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'{FtsMigrationId}';");
            Assert.Equal(1, rowsDeleted);

            // D. Verify partial state: objects present, history absent, migration pending.
            var catalogBeforeRetry = await ScalarAsync(dbConnectionString,
                "SELECT COUNT(*) FROM sys.fulltext_catalogs WHERE [name] = N'OpsFlowFullTextCatalog';");
            var indexBeforeRetry = await ScalarAsync(dbConnectionString,
                "SELECT COUNT(*) FROM sys.fulltext_indexes WHERE [object_id] = OBJECT_ID(N'[DocumentChunks]');");
            var historyBeforeRetry = await ScalarAsync(dbConnectionString,
                $"SELECT COUNT(*) FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'{FtsMigrationId}';");
            Assert.Equal(1, catalogBeforeRetry);
            Assert.Equal(1, indexBeforeRetry);
            Assert.Equal(0, historyBeforeRetry);

            int pendingBeforeRetry;
            await using (var pendingCtx = CreateContext(dbConnectionString))
            {
                pendingBeforeRetry = (await pendingCtx.Database.GetPendingMigrationsAsync()).Count();
            }
            Assert.Equal(1, pendingBeforeRetry);

            // E. Retry the migration.
            await using (var context = CreateContext(dbConnectionString))
            {
                await context.Database.MigrateAsync();
            }

            // F. Full invariant: FTS objects intact AND migration marked applied.
            var catalogAfterRetry = await ScalarAsync(dbConnectionString,
                "SELECT COUNT(*) FROM sys.fulltext_catalogs WHERE [name] = N'OpsFlowFullTextCatalog';");
            var indexAfterRetry = await ScalarAsync(dbConnectionString,
                "SELECT COUNT(*) FROM sys.fulltext_indexes WHERE [object_id] = OBJECT_ID(N'[DocumentChunks]');");
            var historyAfterRetry = await ScalarAsync(dbConnectionString,
                $"SELECT COUNT(*) FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'{FtsMigrationId}';");

            int pendingAfterRetry;
            await using (var pendingCtx = CreateContext(dbConnectionString))
            {
                pendingAfterRetry = (await pendingCtx.Database.GetPendingMigrationsAsync()).Count();
            }

            Assert.Equal(1, catalogAfterRetry);
            Assert.Equal(1, indexAfterRetry);
            Assert.Equal(1, historyAfterRetry);
            Assert.Equal(0, pendingAfterRetry);
        }
        finally
        {
            await TryDropDatabaseAsync(masterConnectionString, dbName);
        }
    }

    private string WithCatalog(string databaseName)
        => new SqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

    private static OpsFlowDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<OpsFlowDbContext>()
            .UseSqlServer(connectionString)
            .Options);

    private static async Task<int> ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task TryDropDatabaseAsync(string masterConnectionString, string dbName)
    {
        try
        {
            SqlConnection.ClearAllPools();
            await ExecuteNonQueryAsync(masterConnectionString,
                $"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}];");
        }
        catch (SqlException)
        {
        }
    }
}
