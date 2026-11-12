using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContentGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContentGovernance",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewOwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Classification = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    LifecycleStatus = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentGovernance", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_ContentGovernance_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_ContentGovernance_Content_TenantId_Id",
                        columns: x => new { x.TenantId, x.Id },
                        principalSchema: "content",
                        principalTable: "Content",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentGovernance_TenantId_Classification",
                schema: "content",
                table: "ContentGovernance",
                columns: new[] { "TenantId", "Classification" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentGovernance_TenantId_LifecycleStatus",
                schema: "content",
                table: "ContentGovernance",
                columns: new[] { "TenantId", "LifecycleStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentGovernance_TenantId_OwnerUserId",
                schema: "content",
                table: "ContentGovernance",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentGovernance_TenantId_ReviewAt",
                schema: "content",
                table: "ContentGovernance",
                columns: new[] { "TenantId", "ReviewAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentGovernance_TenantId_ReviewOwnerUserId",
                schema: "content",
                table: "ContentGovernance",
                columns: new[] { "TenantId", "ReviewOwnerUserId" });

            migrationBuilder.Sql("ALTER TABLE [content].[ContentGovernance] ADD CONSTRAINT [FK_ContentGovernance_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("content", "ContentGovernance");

            // Backfill: Owner = CreatedBy; Classification = Internal; lifecycle from Content.Status (1 Current, 2 Archived, 3 Trashed, 4 Draft).
            migrationBuilder.Sql("""
                INSERT INTO [content].[ContentGovernance] ([Id], [TenantId], [OwnerUserId], [ReviewOwnerUserId], [ReviewAt], [LastReviewedAt], [Classification], [LifecycleStatus], [UpdatedBy], [UpdatedAt])
                SELECT c.[Id], c.[TenantId], c.[CreatedBy], NULL, NULL, NULL, 'Internal',
                    CASE c.[Status] WHEN 4 THEN 'Draft' WHEN 2 THEN 'Archived' WHEN 3 THEN 'Archived' ELSE 'Current' END,
                    c.[UpdatedBy], c.[UpdatedAt]
                FROM [content].[Content] c
                WHERE NOT EXISTS (SELECT 1 FROM [content].[ContentGovernance] g WHERE g.[TenantId] = c.[TenantId] AND g.[Id] = c.[Id]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("content", "ContentGovernance");
            migrationBuilder.DropTable(
                name: "ContentGovernance",
                schema: "content");
        }
    }
}
