using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KnowledgeRelationships",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceContentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelationshipType = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TargetKind = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TargetContentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetExternalKey = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    TargetTitle = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Origin = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SourceSystem = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeRelationships", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_KnowledgeRelationships_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    // Cascade only on source: SQL Server rejects two CASCADE paths to Content.
                    table.ForeignKey(
                        name: "FK_KnowledgeRelationships_Content_TenantId_SourceContentId",
                        columns: x => new { x.TenantId, x.SourceContentId },
                        principalSchema: "content",
                        principalTable: "Content",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeRelationships_Content_TenantId_TargetContentId",
                        columns: x => new { x.TenantId, x.TargetContentId },
                        principalSchema: "content",
                        principalTable: "Content",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeRelationships_TenantId_RelationshipType",
                schema: "content",
                table: "KnowledgeRelationships",
                columns: new[] { "TenantId", "RelationshipType" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeRelationships_TenantId_SourceContentId_RelationshipType",
                schema: "content",
                table: "KnowledgeRelationships",
                columns: new[] { "TenantId", "SourceContentId", "RelationshipType" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeRelationships_TenantId_SourceContentId_RelationshipType_TargetKind_TargetContentId",
                schema: "content",
                table: "KnowledgeRelationships",
                columns: new[] { "TenantId", "SourceContentId", "RelationshipType", "TargetKind", "TargetContentId" },
                unique: true,
                filter: "[TargetContentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeRelationships_TenantId_SourceContentId_RelationshipType_TargetKind_TargetExternalKey",
                schema: "content",
                table: "KnowledgeRelationships",
                columns: new[] { "TenantId", "SourceContentId", "RelationshipType", "TargetKind", "TargetExternalKey" },
                unique: true,
                filter: "[TargetExternalKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeRelationships_TenantId_TargetContentId",
                schema: "content",
                table: "KnowledgeRelationships",
                columns: new[] { "TenantId", "TargetContentId" });

            migrationBuilder.Sql("ALTER TABLE [content].[KnowledgeRelationships] ADD CONSTRAINT [FK_KnowledgeRelationships_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("content", "KnowledgeRelationships");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("content", "KnowledgeRelationships");
            migrationBuilder.DropTable(
                name: "KnowledgeRelationships",
                schema: "content");
        }
    }
}
