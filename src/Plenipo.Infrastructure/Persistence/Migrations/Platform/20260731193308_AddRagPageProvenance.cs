using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plenipo.Infrastructure.Persistence.Migrations.Platform
{
    /// <summary>
    /// Page provenance on retrieved passages, so a cited answer can say "p. 7" instead of only
    /// naming a file. Nullable because it is genuinely unknown for sources with no pages (plain
    /// text) or extractors that cannot report them — a null cites the file alone, which is honest;
    /// a default of 1 would be a fabricated citation. Existing chunks stay null until re-indexed.
    /// </summary>
    public partial class AddRagPageProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PageFrom",
                schema: "platform",
                table: "rag_chunks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PageTo",
                schema: "platform",
                table: "rag_chunks",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PageFrom",
                schema: "platform",
                table: "rag_chunks");

            migrationBuilder.DropColumn(
                name: "PageTo",
                schema: "platform",
                table: "rag_chunks");
        }
    }
}
