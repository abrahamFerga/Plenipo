using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plenipo.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class ApprovalRequesterSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PermissionsSnapshotJson",
                schema: "platform",
                table: "pending_approvals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequesterSubject",
                schema: "platform",
                table: "pending_approvals",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PermissionsSnapshotJson",
                schema: "platform",
                table: "pending_approvals");

            migrationBuilder.DropColumn(
                name: "RequesterSubject",
                schema: "platform",
                table: "pending_approvals");
        }
    }
}
