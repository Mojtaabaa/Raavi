using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WhiteboardPhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                schema: "content",
                table: "Whiteboards",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Background",
                schema: "content",
                table: "Whiteboards",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: false,
                defaultValue: "#ffffff");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "content",
                table: "Whiteboards",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GridStyle",
                schema: "content",
                table: "Whiteboards",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: false,
                defaultValue: "Dot");

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedBy",
                schema: "content",
                table: "Whiteboards",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.Sql("UPDATE [content].[Whiteboards] SET [UpdatedBy] = [CreatedBy] WHERE [UpdatedBy] = '00000000-0000-0000-0000-000000000000';");

            migrationBuilder.CreateTable(
                name: "WhiteboardAttachments",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WhiteboardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    MediaType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Data = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhiteboardAttachments", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_WhiteboardAttachments_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_WhiteboardAttachments_Whiteboards_TenantId_WhiteboardId",
                        columns: x => new { x.TenantId, x.WhiteboardId },
                        principalSchema: "content",
                        principalTable: "Whiteboards",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WhiteboardComments",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WhiteboardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ObjectId = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    X = table.Column<double>(type: "float", nullable: true),
                    Y = table.Column<double>(type: "float", nullable: true),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhiteboardComments", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_WhiteboardComments_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_WhiteboardComments_Whiteboards_TenantId_WhiteboardId",
                        columns: x => new { x.TenantId, x.WhiteboardId },
                        principalSchema: "content",
                        principalTable: "Whiteboards",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WhiteboardFavorites",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WhiteboardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhiteboardFavorites", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_WhiteboardFavorites_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_WhiteboardFavorites_Whiteboards_TenantId_WhiteboardId",
                        columns: x => new { x.TenantId, x.WhiteboardId },
                        principalSchema: "content",
                        principalTable: "Whiteboards",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WhiteboardRevisions",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WhiteboardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BoardRevision = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ElementsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Background = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    GridStyle = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhiteboardRevisions", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_WhiteboardRevisions_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_WhiteboardRevisions_Whiteboards_TenantId_WhiteboardId",
                        columns: x => new { x.TenantId, x.WhiteboardId },
                        principalSchema: "content",
                        principalTable: "Whiteboards",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WhiteboardTemplates",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ElementsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Background = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    GridStyle = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhiteboardTemplates", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_WhiteboardTemplates_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Whiteboards_TenantId_SpaceId_ArchivedAt",
                schema: "content",
                table: "Whiteboards",
                columns: new[] { "TenantId", "SpaceId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WhiteboardAttachments_TenantId_WhiteboardId",
                schema: "content",
                table: "WhiteboardAttachments",
                columns: new[] { "TenantId", "WhiteboardId" });

            migrationBuilder.CreateIndex(
                name: "IX_WhiteboardComments_TenantId_WhiteboardId_CreatedAt",
                schema: "content",
                table: "WhiteboardComments",
                columns: new[] { "TenantId", "WhiteboardId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WhiteboardFavorites_TenantId_UserId_WhiteboardId",
                schema: "content",
                table: "WhiteboardFavorites",
                columns: new[] { "TenantId", "UserId", "WhiteboardId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WhiteboardFavorites_TenantId_WhiteboardId",
                schema: "content",
                table: "WhiteboardFavorites",
                columns: new[] { "TenantId", "WhiteboardId" });

            migrationBuilder.CreateIndex(
                name: "IX_WhiteboardRevisions_TenantId_WhiteboardId_CreatedAt",
                schema: "content",
                table: "WhiteboardRevisions",
                columns: new[] { "TenantId", "WhiteboardId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WhiteboardTemplates_TenantId_SpaceId_Name",
                schema: "content",
                table: "WhiteboardTemplates",
                columns: new[] { "TenantId", "SpaceId", "Name" });

            // raavi:rls-patched — tenant FKs and Row-Level Security (TENANT_ISOLATION.md)
            migrationBuilder.Sql("ALTER TABLE [content].[WhiteboardAttachments] ADD CONSTRAINT [FK_WhiteboardAttachments_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [content].[WhiteboardComments] ADD CONSTRAINT [FK_WhiteboardComments_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [content].[WhiteboardFavorites] ADD CONSTRAINT [FK_WhiteboardFavorites_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [content].[WhiteboardRevisions] ADD CONSTRAINT [FK_WhiteboardRevisions_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [content].[WhiteboardTemplates] ADD CONSTRAINT [FK_WhiteboardTemplates_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("content", "WhiteboardAttachments");
            migrationBuilder.AddTenantIsolation("content", "WhiteboardComments");
            migrationBuilder.AddTenantIsolation("content", "WhiteboardFavorites");
            migrationBuilder.AddTenantIsolation("content", "WhiteboardRevisions");
            migrationBuilder.AddTenantIsolation("content", "WhiteboardTemplates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("content", "WhiteboardAttachments");
            migrationBuilder.DropTenantIsolation("content", "WhiteboardComments");
            migrationBuilder.DropTenantIsolation("content", "WhiteboardFavorites");
            migrationBuilder.DropTenantIsolation("content", "WhiteboardRevisions");
            migrationBuilder.DropTenantIsolation("content", "WhiteboardTemplates");

            migrationBuilder.DropTable(
                name: "WhiteboardAttachments",
                schema: "content");

            migrationBuilder.DropTable(
                name: "WhiteboardComments",
                schema: "content");

            migrationBuilder.DropTable(
                name: "WhiteboardFavorites",
                schema: "content");

            migrationBuilder.DropTable(
                name: "WhiteboardRevisions",
                schema: "content");

            migrationBuilder.DropTable(
                name: "WhiteboardTemplates",
                schema: "content");

            migrationBuilder.DropIndex(
                name: "IX_Whiteboards_TenantId_SpaceId_ArchivedAt",
                schema: "content",
                table: "Whiteboards");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                schema: "content",
                table: "Whiteboards");

            migrationBuilder.DropColumn(
                name: "Background",
                schema: "content",
                table: "Whiteboards");

            migrationBuilder.DropColumn(
                name: "Description",
                schema: "content",
                table: "Whiteboards");

            migrationBuilder.DropColumn(
                name: "GridStyle",
                schema: "content",
                table: "Whiteboards");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                schema: "content",
                table: "Whiteboards");
        }
    }
}
