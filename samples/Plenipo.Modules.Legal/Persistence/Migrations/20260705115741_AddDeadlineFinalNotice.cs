using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plenipo.Modules.Legal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadlineFinalNotice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinalNoticeSentAt",
                schema: "legal",
                table: "matter_deadlines",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinalNoticeSentAt",
                schema: "legal",
                table: "matter_deadlines");
        }
    }
}
