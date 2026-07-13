using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plenipo.Modules.Legal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatterWalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RestrictedUserIdsJson",
                schema: "legal",
                table: "matters",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RestrictedUserIdsJson",
                schema: "legal",
                table: "matters");
        }
    }
}
