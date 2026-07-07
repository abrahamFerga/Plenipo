using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cortex.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class AddTenantMaxSeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxSeats",
                schema: "platform",
                table: "tenants",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxSeats",
                schema: "platform",
                table: "tenants");
        }
    }
}
