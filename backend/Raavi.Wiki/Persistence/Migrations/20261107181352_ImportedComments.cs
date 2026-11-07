using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportedComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "AuthorId",
                schema: "content",
                table: "Comments",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "SourceAuthorName",
                schema: "content",
                table: "Comments",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceId",
                schema: "content",
                table: "Comments",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "SourceSystem",
                schema: "content",
                table: "Comments",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Comments_TenantId_SourceSystem_SourceId",
                schema: "content",
                table: "Comments",
                columns: new[] { "TenantId", "SourceSystem", "SourceId" },
                unique: true,
                filter: "[SourceSystem] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Comments_TenantId_SourceSystem_SourceId",
                schema: "content",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "SourceAuthorName",
                schema: "content",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "SourceId",
                schema: "content",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "SourceSystem",
                schema: "content",
                table: "Comments");

            migrationBuilder.AlterColumn<Guid>(
                name: "AuthorId",
                schema: "content",
                table: "Comments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
