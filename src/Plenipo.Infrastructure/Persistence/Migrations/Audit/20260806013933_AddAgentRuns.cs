using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plenipo.Infrastructure.Persistence.Migrations.Audit
{
    /// <inheritdoc />
    public partial class AddAgentRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_runs",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserDisplay = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ModuleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    WorkflowName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ParentRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    InstructionsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ErrorKind = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FirstTokenMs = table.Column<long>(type: "bigint", nullable: true),
                    TotalMs = table.Column<long>(type: "bigint", nullable: false),
                    ToolCallCount = table.Column<int>(type: "integer", nullable: false),
                    ApprovalCount = table.Column<int>(type: "integer", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    TotalTokens = table.Column<long>(type: "bigint", nullable: false),
                    CachedInputTokens = table.Column<long>(type: "bigint", nullable: false),
                    ReasoningTokens = table.Column<long>(type: "bigint", nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    TraceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SpanId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_ConversationId",
                schema: "audit",
                table: "agent_runs",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_ParentRunId",
                schema: "audit",
                table: "agent_runs",
                column: "ParentRunId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_TenantId_OccurredAt",
                schema: "audit",
                table: "agent_runs",
                columns: new[] { "TenantId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_TenantId_Outcome_OccurredAt",
                schema: "audit",
                table: "agent_runs",
                columns: new[] { "TenantId", "Outcome", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_runs",
                schema: "audit");
        }
    }
}
