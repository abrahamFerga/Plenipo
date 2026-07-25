using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plenipo.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class AddApprovalSurfacedToModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SurfacedToModelAt",
                schema: "platform",
                table: "pending_approvals",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SurfacedToModelAt",
                schema: "platform",
                table: "pending_approvals");
        }
    }
}
