using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raavi.AI.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SearchNormalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The folded columns are filled by the indexer: forget what was indexed so every page is cut again. RLS has no bypass for the
            // migrator, so the policy is off inside this one transaction (as in CompressPageVersions); an upgrade stops the application first.
            migrationBuilder.Sql("ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = OFF);");
            migrationBuilder.Sql("DELETE FROM [ai].[Chunks]; DELETE FROM [ai].[IndexedPages];");
            migrationBuilder.Sql("ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = ON);");

            migrationBuilder.AddColumn<string>(
                name: "Norm",
                schema: "ai",
                table: "Chunks",
                type: "nvarchar(1300)",
                maxLength: 1300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TitleNorm",
                schema: "ai",
                table: "Chunks",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Norm",
                schema: "ai",
                table: "Chunks");

            migrationBuilder.DropColumn(
                name: "TitleNorm",
                schema: "ai",
                table: "Chunks");
        }
    }
}
