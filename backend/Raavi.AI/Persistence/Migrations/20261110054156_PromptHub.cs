using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.AI.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PromptHub : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PromptCategories",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptCategories", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_PromptCategories_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "PromptEvents",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    PromptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Context = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    Mode = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: true),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PromptTokens = table.Column<int>(type: "int", nullable: true),
                    CompletionTokens = table.Column<int>(type: "int", nullable: true),
                    ErrorClass = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptEvents", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_PromptEvents_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "Prompts",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Visibility = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Tags = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    Language = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    ContextKinds = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Modes = table.Column<int>(type: "int", nullable: false),
                    DraftVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LatestVersionNumber = table.Column<int>(type: "int", nullable: false),
                    ForkedFromId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ForkedFromOwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ForkedFromTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Prompts", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_Prompts_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "PromptVersions",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PromptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    IsPublished = table.Column<bool>(type: "bit", nullable: false),
                    ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChangeNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptVersions", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_PromptVersions_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PromptCategories_TenantId_ParentId_SortOrder",
                schema: "ai",
                table: "PromptCategories",
                columns: new[] { "TenantId", "ParentId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PromptEvents_TenantId_PromptId_At",
                schema: "ai",
                table: "PromptEvents",
                columns: new[] { "TenantId", "PromptId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PromptEvents_TenantId_Type_At",
                schema: "ai",
                table: "PromptEvents",
                columns: new[] { "TenantId", "Type", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PromptEvents_TenantId_UserId_At",
                schema: "ai",
                table: "PromptEvents",
                columns: new[] { "TenantId", "UserId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Prompts_TenantId_CategoryId",
                schema: "ai",
                table: "Prompts",
                columns: new[] { "TenantId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Prompts_TenantId_ForkedFromId",
                schema: "ai",
                table: "Prompts",
                columns: new[] { "TenantId", "ForkedFromId" });

            migrationBuilder.CreateIndex(
                name: "IX_Prompts_TenantId_OwnerId_Status_UpdatedAt",
                schema: "ai",
                table: "Prompts",
                columns: new[] { "TenantId", "OwnerId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Prompts_TenantId_Visibility_Status_PublishedAt",
                schema: "ai",
                table: "Prompts",
                columns: new[] { "TenantId", "Visibility", "Status", "PublishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PromptVersions_TenantId_PromptId_Number",
                schema: "ai",
                table: "PromptVersions",
                columns: new[] { "TenantId", "PromptId", "Number" },
                unique: true);

            // raavi:rls-patched — tenant FKs and Row-Level Security (TENANT_ISOLATION.md)
            migrationBuilder.Sql("ALTER TABLE [ai].[PromptCategories] ADD CONSTRAINT [FK_PromptCategories_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [ai].[PromptEvents] ADD CONSTRAINT [FK_PromptEvents_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [ai].[Prompts] ADD CONSTRAINT [FK_Prompts_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [ai].[PromptVersions] ADD CONSTRAINT [FK_PromptVersions_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("ai", "PromptCategories");
            migrationBuilder.AddTenantIsolation("ai", "PromptEvents");
            migrationBuilder.AddTenantIsolation("ai", "Prompts");
            migrationBuilder.AddTenantIsolation("ai", "PromptVersions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("ai", "PromptCategories");
            migrationBuilder.DropTenantIsolation("ai", "PromptEvents");
            migrationBuilder.DropTenantIsolation("ai", "Prompts");
            migrationBuilder.DropTenantIsolation("ai", "PromptVersions");

            migrationBuilder.DropTable(
                name: "PromptCategories",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "PromptEvents",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "Prompts",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "PromptVersions",
                schema: "ai");
        }
    }
}
