using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cortex.Modules.Legal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialLegal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "legal");

            migrationBuilder.CreateTable(
                name: "matters",
                schema: "legal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ClientName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "matter_documents",
                schema: "legal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MatterId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matter_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_matter_documents_matters_MatterId",
                        column: x => x.MatterId,
                        principalSchema: "legal",
                        principalTable: "matters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_matter_documents_MatterId",
                schema: "legal",
                table: "matter_documents",
                column: "MatterId");

            migrationBuilder.CreateIndex(
                name: "IX_matter_documents_MatterId_FileId",
                schema: "legal",
                table: "matter_documents",
                columns: new[] { "MatterId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_matters_TenantId_Name",
                schema: "legal",
                table: "matters",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "matter_documents",
                schema: "legal");

            migrationBuilder.DropTable(
                name: "matters",
                schema: "legal");
        }
    }
}
