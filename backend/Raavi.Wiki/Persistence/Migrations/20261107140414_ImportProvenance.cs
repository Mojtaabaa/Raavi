using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceId",
                schema: "spaces",
                table: "Spaces",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "SourceSystem",
                schema: "spaces",
                table: "Spaces",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceAuthorJson",
                schema: "content",
                table: "PageVersions",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceVersionIdentifier",
                schema: "content",
                table: "PageVersions",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceId",
                schema: "content",
                table: "Content",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "SourceSystem",
                schema: "content",
                table: "Content",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                schema: "content",
                table: "Content",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Spaces_TenantId_SourceSystem_SourceId",
                schema: "spaces",
                table: "Spaces",
                columns: new[] { "TenantId", "SourceSystem", "SourceId" },
                unique: true,
                filter: "[SourceSystem] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PageVersions_TenantId_ContentId_SourceVersionIdentifier",
                schema: "content",
                table: "PageVersions",
                columns: new[] { "TenantId", "ContentId", "SourceVersionIdentifier" },
                unique: true,
                filter: "[SourceVersionIdentifier] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Content_TenantId_SourceSystem_SourceId",
                schema: "content",
                table: "Content",
                columns: new[] { "TenantId", "SourceSystem", "SourceId" },
                unique: true,
                filter: "[SourceSystem] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Spaces_TenantId_SourceSystem_SourceId",
                schema: "spaces",
                table: "Spaces");

            migrationBuilder.DropIndex(
                name: "IX_PageVersions_TenantId_ContentId_SourceVersionIdentifier",
                schema: "content",
                table: "PageVersions");

            migrationBuilder.DropIndex(
                name: "IX_Content_TenantId_SourceSystem_SourceId",
                schema: "content",
                table: "Content");

            migrationBuilder.DropColumn(
                name: "SourceId",
                schema: "spaces",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "SourceSystem",
                schema: "spaces",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "SourceAuthorJson",
                schema: "content",
                table: "PageVersions");

            migrationBuilder.DropColumn(
                name: "SourceVersionIdentifier",
                schema: "content",
                table: "PageVersions");

            migrationBuilder.DropColumn(
                name: "SourceId",
                schema: "content",
                table: "Content");

            migrationBuilder.DropColumn(
                name: "SourceSystem",
                schema: "content",
                table: "Content");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                schema: "content",
                table: "Content");
        }
    }
}
