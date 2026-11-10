using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.AI.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SmartSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                schema: "ai",
                table: "Settings",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Chunks",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SpaceKey = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1300)", maxLength: 1300, nullable: false),
                    Embedding = table.Column<byte[]>(type: "varbinary(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chunks", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_Chunks_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "IndexedPages",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChunkCount = table.Column<int>(type: "int", nullable: false),
                    EmbeddingModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IndexedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexedPages", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_IndexedPages_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_TenantId_ContentId_Position",
                schema: "ai",
                table: "Chunks",
                columns: new[] { "TenantId", "ContentId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_TenantId_SpaceKey",
                schema: "ai",
                table: "Chunks",
                columns: new[] { "TenantId", "SpaceKey" });

            migrationBuilder.CreateIndex(
                name: "IX_IndexedPages_TenantId_ContentId",
                schema: "ai",
                table: "IndexedPages",
                columns: new[] { "TenantId", "ContentId" },
                unique: true);

            // raavi:rls-patched — tenant FKs and Row-Level Security (TENANT_ISOLATION.md)
            migrationBuilder.Sql("ALTER TABLE [ai].[Chunks] ADD CONSTRAINT [FK_Chunks_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [ai].[IndexedPages] ADD CONSTRAINT [FK_IndexedPages_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("ai", "Chunks");
            migrationBuilder.AddTenantIsolation("ai", "IndexedPages");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("ai", "Chunks");
            migrationBuilder.DropTenantIsolation("ai", "IndexedPages");

            migrationBuilder.DropTable(
                name: "Chunks",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "IndexedPages",
                schema: "ai");

            migrationBuilder.DropColumn(
                name: "EmbeddingModel",
                schema: "ai",
                table: "Settings");
        }
    }
}
