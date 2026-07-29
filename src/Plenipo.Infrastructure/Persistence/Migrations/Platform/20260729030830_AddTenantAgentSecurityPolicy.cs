using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plenipo.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class AddTenantAgentSecurityPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentSecurityMode",
                schema: "platform",
                table: "tenant_ai_settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ContentSafetyEnabled",
                schema: "platform",
                table: "tenant_ai_settings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PromptAttackDetectionEnabled",
                schema: "platform",
                table: "tenant_ai_settings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SensitiveDataHandling",
                schema: "platform",
                table: "tenant_ai_settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentSecurityMode",
                schema: "platform",
                table: "tenant_ai_settings");

            migrationBuilder.DropColumn(
                name: "ContentSafetyEnabled",
                schema: "platform",
                table: "tenant_ai_settings");

            migrationBuilder.DropColumn(
                name: "PromptAttackDetectionEnabled",
                schema: "platform",
                table: "tenant_ai_settings");

            migrationBuilder.DropColumn(
                name: "SensitiveDataHandling",
                schema: "platform",
                table: "tenant_ai_settings");
        }
    }
}
