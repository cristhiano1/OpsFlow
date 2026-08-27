using System;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentEmbeddingSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChunkingVersion = table.Column<int>(type: "int", nullable: false),
                    ProfileId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    EmbeddingCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentEmbeddingSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentEmbeddingSets_DocumentChunkSets_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "DocumentChunkSets",
                        principalColumn: "DocumentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentChunkEmbeddings",
                columns: table => new
                {
                    EmbeddingSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentChunkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Embedding = table.Column<SqlVector<float>>(type: "vector(1536)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentChunkEmbeddings", x => new { x.EmbeddingSetId, x.DocumentChunkId });
                    table.ForeignKey(
                        name: "FK_DocumentChunkEmbeddings_DocumentChunks_DocumentChunkId",
                        column: x => x.DocumentChunkId,
                        principalTable: "DocumentChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentChunkEmbeddings_DocumentEmbeddingSets_EmbeddingSetId",
                        column: x => x.EmbeddingSetId,
                        principalTable: "DocumentEmbeddingSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunkEmbeddings_DocumentChunkId",
                table: "DocumentChunkEmbeddings",
                column: "DocumentChunkId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentEmbeddingSets_DocumentId_ProfileId",
                table: "DocumentEmbeddingSets",
                columns: new[] { "DocumentId", "ProfileId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentChunkEmbeddings");

            migrationBuilder.DropTable(
                name: "DocumentEmbeddingSets");
        }
    }
}
