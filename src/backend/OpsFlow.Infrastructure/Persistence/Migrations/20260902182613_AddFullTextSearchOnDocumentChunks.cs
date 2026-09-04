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
            // Full-text DDL cannot run inside a user transaction, so these
            // statements use suppressTransaction: true. That makes them
            // non-atomic with the __EFMigrationsHistory update: an interrupted
            // deployment can leave the catalog and/or index created while EF has
            // not recorded this migration. The existence guards below make the
            // migration safe to re-run in that partial state.
            migrationBuilder.Sql(
                """
                IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE [name] = N'OpsFlowFullTextCatalog')
                BEGIN
                    EXEC(N'CREATE FULLTEXT CATALOG [OpsFlowFullTextCatalog] AS DEFAULT;');
                END
                """,
                suppressTransaction: true);

            migrationBuilder.Sql(
                """
                IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE [object_id] = OBJECT_ID(N'[DocumentChunks]'))
                BEGIN
                    EXEC(N'
                        CREATE FULLTEXT INDEX ON [DocumentChunks]
                        (
                            [Text] LANGUAGE 0
                        )
                        KEY INDEX [PK_DocumentChunks]
                        ON [OpsFlowFullTextCatalog]
                        WITH CHANGE_TRACKING AUTO;');
                END
                """,
                suppressTransaction: true);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop the index before the catalog (a catalog cannot be dropped
            // while it still holds a full-text index). Existence guards keep the
            // teardown safe when the objects were already removed.
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE [object_id] = OBJECT_ID(N'[DocumentChunks]'))
                BEGIN
                    EXEC(N'DROP FULLTEXT INDEX ON [DocumentChunks];');
                END
                """,
                suppressTransaction: true);

            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE [name] = N'OpsFlowFullTextCatalog')
                BEGIN
                    EXEC(N'DROP FULLTEXT CATALOG [OpsFlowFullTextCatalog];');
                END
                """,
                suppressTransaction: true);
        }
    }
}
