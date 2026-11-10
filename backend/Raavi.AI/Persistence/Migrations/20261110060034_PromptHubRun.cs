using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.AI.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PromptHubRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VariablesJson",
                schema: "ai",
                table: "PromptVersions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "PromptExecutions",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PromptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContextKind = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: true),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PromptTokens = table.Column<int>(type: "int", nullable: true),
                    CompletionTokens = table.Column<int>(type: "int", nullable: true),
                    ErrorClass = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    EstimatedTokens = table.Column<int>(type: "int", nullable: false),
                    SourcesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResultText = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptExecutions", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_PromptExecutions_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "PromptRatings",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PromptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Stars = table.Column<int>(type: "int", nullable: false),
                    FeedbackKind = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptRatings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_PromptRatings_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PromptExecutions_TenantId_PromptId_StartedAt",
                schema: "ai",
                table: "PromptExecutions",
                columns: new[] { "TenantId", "PromptId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PromptExecutions_TenantId_UserId_StartedAt",
                schema: "ai",
                table: "PromptExecutions",
                columns: new[] { "TenantId", "UserId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PromptRatings_TenantId_PromptId_UserId",
                schema: "ai",
                table: "PromptRatings",
                columns: new[] { "TenantId", "PromptId", "UserId" },
                unique: true);
        
            // raavi:rls-patched — tenant FKs and Row-Level Security (TENANT_ISOLATION.md)
            migrationBuilder.Sql("ALTER TABLE [ai].[PromptExecutions] ADD CONSTRAINT [FK_PromptExecutions_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [ai].[PromptRatings] ADD CONSTRAINT [FK_PromptRatings_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("ai", "PromptExecutions");
            migrationBuilder.AddTenantIsolation("ai", "PromptRatings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("ai", "PromptExecutions");
            migrationBuilder.DropTenantIsolation("ai", "PromptRatings");

            migrationBuilder.DropTable(
                name: "PromptExecutions",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "PromptRatings",
                schema: "ai");

            migrationBuilder.DropColumn(
                name: "VariablesJson",
                schema: "ai",
                table: "PromptVersions");
        }
    }
}
