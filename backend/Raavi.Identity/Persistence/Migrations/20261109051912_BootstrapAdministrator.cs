using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raavi.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BootstrapAdministrator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                schema: "identity",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                schema: "identity",
                table: "Users");
        }
    }
}
