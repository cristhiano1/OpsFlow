using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFullTextSearchOnDocumentChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE FULLTEXT CATALOG [OpsFlowFullTextCatalog] AS DEFAULT;",
                suppressTransaction: true);

            migrationBuilder.Sql(
                """
                CREATE FULLTEXT INDEX ON [DocumentChunks]
                (
                    [Text] LANGUAGE 0
                )
                KEY INDEX [PK_DocumentChunks]
                ON [OpsFlowFullTextCatalog]
                WITH CHANGE_TRACKING AUTO;
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP FULLTEXT INDEX ON [DocumentChunks];",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "DROP FULLTEXT CATALOG [OpsFlowFullTextCatalog];",
                suppressTransaction: true);
        }
    }
}
