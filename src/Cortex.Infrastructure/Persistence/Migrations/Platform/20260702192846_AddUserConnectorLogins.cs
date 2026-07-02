using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cortex.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class AddUserConnectorLogins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_connector_logins",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProtectedTokensJson = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_connector_logins", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_connector_logins_TenantId_ConnectorId",
                schema: "platform",
                table: "user_connector_logins",
                columns: new[] { "TenantId", "ConnectorId" });

            migrationBuilder.CreateIndex(
                name: "IX_user_connector_logins_UserId_ConnectorId",
                schema: "platform",
                table: "user_connector_logins",
                columns: new[] { "UserId", "ConnectorId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_connector_logins",
                schema: "platform");
        }
    }
}
