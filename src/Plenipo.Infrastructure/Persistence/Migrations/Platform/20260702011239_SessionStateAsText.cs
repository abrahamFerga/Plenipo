using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plenipo.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class SessionStateAsText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Postgres has no automatic jsonb -> text cast, and any state stored while the column was
            // jsonb has had its JSON key order mangled (poisonous to System.Text.Json's polymorphic
            // $type rehydration) — so change the type AND drop the old values in one statement. The
            // agent runner reseeds a conversation's session from its replayed message history.
            migrationBuilder.Sql(
                @"ALTER TABLE platform.conversations ALTER COLUMN ""SessionState"" TYPE text USING NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"ALTER TABLE platform.conversations ALTER COLUMN ""SessionState"" TYPE jsonb USING NULL;");
        }
    }
}
