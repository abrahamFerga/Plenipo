using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cortex.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class AddCommerceCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BillingEvents",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EventId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantEntitlements",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Plan = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Seats = table.Column<int>(type: "integer", nullable: true),
                    SubscriptionRef = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CustomerRef = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeprovisionAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantEntitlements", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingEvents_ProcessedAt",
                schema: "platform",
                table: "BillingEvents",
                column: "ProcessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BillingEvents_Provider_EventId",
                schema: "platform",
                table: "BillingEvents",
                columns: new[] { "Provider", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantEntitlements_SubscriptionRef",
                schema: "platform",
                table: "TenantEntitlements",
                column: "SubscriptionRef",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantEntitlements_TenantId",
                schema: "platform",
                table: "TenantEntitlements",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingEvents",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "TenantEntitlements",
                schema: "platform");
        }
    }
}
