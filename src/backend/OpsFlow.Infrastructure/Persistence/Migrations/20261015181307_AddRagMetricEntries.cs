using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRagMetricEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RagMetricEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RetrievalMode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FailureCategory = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SelectedEvidenceCount = table.Column<int>(type: "int", nullable: true),
                    TotalDurationSeconds = table.Column<double>(type: "float", nullable: false),
                    RetrievalDurationSeconds = table.Column<double>(type: "float", nullable: false),
                    GenerationDurationSeconds = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RagMetricEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RagMetricEntries_OrganizationId_RecordedAt",
                table: "RagMetricEntries",
                columns: new[] { "OrganizationId", "RecordedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RagMetricEntries");
        }
    }
}
