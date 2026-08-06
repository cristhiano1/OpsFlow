using System.Data;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.IntegrationTests.Authentication;

/// <summary>
/// Verifies the <c>AspNetUsers → Organizations</c> foreign key is left in the
/// same enabled + trusted state that migrations produce. Tests that briefly
/// disable this FK to plant an orphan row must call this probe after their
/// cleanup so no untrusted metadata (<c>is_not_trusted = 1</c>) leaks into
/// the shared <see cref="Infrastructure.SqlServerFixture"/> and skews sibling
/// tests in the collection.
/// </summary>
internal static class UserOrgForeignKeyProbe
{
    private const string FkName = "FK_AspNetUsers_Organizations_OrganizationId";

    public static async Task AssertEnabledAndTrustedAsync(OpsFlowDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT is_disabled, is_not_trusted FROM sys.foreign_keys WHERE name = @name;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = FkName;
            _ = command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(
                await reader.ReadAsync(),
                $"Foreign key {FkName} must exist in sys.foreign_keys.");
            Assert.False(
                reader.GetBoolean(0),
                $"Foreign key {FkName} must be enabled after test cleanup (is_disabled = 0).");
            Assert.False(
                reader.GetBoolean(1),
                $"Foreign key {FkName} must be trusted after test cleanup (is_not_trusted = 0).");
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }
}
