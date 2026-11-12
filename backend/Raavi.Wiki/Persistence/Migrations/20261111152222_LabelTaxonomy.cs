using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LabelTaxonomy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                schema: "content",
                table: "Labels",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Color",
                schema: "content",
                table: "Labels",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedBy",
                schema: "content",
                table: "Labels",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "content",
                table: "Labels",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                schema: "content",
                table: "Labels",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "DisplayOrder",
                schema: "content",
                table: "Labels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                schema: "content",
                table: "Labels",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystem",
                schema: "content",
                table: "Labels",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                schema: "content",
                table: "Labels",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                schema: "content",
                table: "Labels",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                schema: "content",
                table: "Labels",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                schema: "content",
                table: "Labels",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedBy",
                schema: "content",
                table: "Labels",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill before unique indexes (empty defaults would collide).
            migrationBuilder.Sql("""
                UPDATE [content].[Labels]
                SET [DisplayName] = [Name],
                    [NormalizedName] = [Name],
                    [Slug] = [Name],
                    [IsActive] = 1,
                    [UpdatedAt] = [CreatedAt]
                WHERE [DisplayName] = N'' OR [Slug] = N'' OR [NormalizedName] = N'';
                """);

            migrationBuilder.CreateTable(
                name: "LabelAliases",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LabelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedAlias = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelAliases", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_LabelAliases_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_LabelAliases_Labels_TenantId_LabelId",
                        columns: x => new { x.TenantId, x.LabelId },
                        principalSchema: "content",
                        principalTable: "Labels",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Labels_TenantId_NormalizedName",
                schema: "content",
                table: "Labels",
                columns: new[] { "TenantId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Labels_TenantId_ParentId_DisplayOrder",
                schema: "content",
                table: "Labels",
                columns: new[] { "TenantId", "ParentId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Labels_TenantId_Slug",
                schema: "content",
                table: "Labels",
                columns: new[] { "TenantId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabelAliases_TenantId_LabelId",
                schema: "content",
                table: "LabelAliases",
                columns: new[] { "TenantId", "LabelId" });

            migrationBuilder.CreateIndex(
                name: "IX_LabelAliases_TenantId_NormalizedAlias",
                schema: "content",
                table: "LabelAliases",
                columns: new[] { "TenantId", "NormalizedAlias" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Labels_Labels_TenantId_ParentId",
                schema: "content",
                table: "Labels",
                columns: new[] { "TenantId", "ParentId" },
                principalSchema: "content",
                principalTable: "Labels",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("ALTER TABLE [content].[LabelAliases] ADD CONSTRAINT [FK_LabelAliases_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("content", "LabelAliases");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("content", "LabelAliases");

            migrationBuilder.DropForeignKey(
                name: "FK_Labels_Labels_TenantId_ParentId",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropTable(
                name: "LabelAliases",
                schema: "content");

            migrationBuilder.DropIndex(
                name: "IX_Labels_TenantId_NormalizedName",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropIndex(
                name: "IX_Labels_TenantId_ParentId_DisplayOrder",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropIndex(
                name: "IX_Labels_TenantId_Slug",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "Color",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "Description",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "DisplayOrder",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "IsActive",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "IsSystem",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "ParentId",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "Slug",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "content",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                schema: "content",
                table: "Labels");
        }
    }
}
