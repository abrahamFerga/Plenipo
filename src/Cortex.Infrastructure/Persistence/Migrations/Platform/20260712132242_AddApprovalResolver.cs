using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cortex.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class AddApprovalResolver : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResolvedByDisplay",
                schema: "platform",
                table: "pending_approvals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResolvedByUserId",
                schema: "platform",
                table: "pending_approvals",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResolvedByDisplay",
                schema: "platform",
                table: "pending_approvals");

            migrationBuilder.DropColumn(
                name: "ResolvedByUserId",
                schema: "platform",
                table: "pending_approvals");
        }
    }
}
