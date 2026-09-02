using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyZeroNormEmbeddingSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM [DocumentEmbeddingSets]
                WHERE [Id] IN
                (
                    SELECT DISTINCT [EmbeddingSetId]
                    FROM [DocumentChunkEmbeddings]
                    WHERE VECTOR_NORM([Embedding], 'norm2') = 0
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: legacy zero-norm embedding sets cannot be safely restored
            // because their generated vector contents are not reconstructible.
        }
    }
}
