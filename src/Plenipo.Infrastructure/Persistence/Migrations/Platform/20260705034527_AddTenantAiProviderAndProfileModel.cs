using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plenipo.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class AddTenantAiProviderAndProfileModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiKeySecretRef",
                schema: "platform",
                table: "tenant_ai_settings",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Endpoint",
                schema: "platform",
                table: "tenant_ai_settings",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                schema: "platform",
                table: "tenant_ai_settings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                schema: "platform",
                table: "tenant_ai_settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                schema: "platform",
                table: "agent_profiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiKeySecretRef",
                schema: "platform",
                table: "tenant_ai_settings");

            migrationBuilder.DropColumn(
                name: "Endpoint",
                schema: "platform",
                table: "tenant_ai_settings");

            migrationBuilder.DropColumn(
                name: "Model",
                schema: "platform",
                table: "tenant_ai_settings");

            migrationBuilder.DropColumn(
                name: "Provider",
                schema: "platform",
                table: "tenant_ai_settings");

            migrationBuilder.DropColumn(
                name: "Model",
                schema: "platform",
                table: "agent_profiles");
        }
    }
}
