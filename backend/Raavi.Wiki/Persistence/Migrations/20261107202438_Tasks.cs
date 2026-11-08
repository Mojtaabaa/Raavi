using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Tasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tasks",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AssigneeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_Tasks_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_Tasks_Spaces_TenantId_SpaceId",
                        columns: x => new { x.TenantId, x.SpaceId },
                        principalSchema: "spaces",
                        principalTable: "Spaces",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_TenantId_AssigneeId_Status",
                schema: "content",
                table: "Tasks",
                columns: new[] { "TenantId", "AssigneeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_TenantId_SpaceId_Status_DueDate",
                schema: "content",
                table: "Tasks",
                columns: new[] { "TenantId", "SpaceId", "Status", "DueDate" });

            // raavi:rls-patched — tenant FKs and Row-Level Security (TENANT_ISOLATION.md)
            migrationBuilder.Sql("ALTER TABLE [content].[Tasks] ADD CONSTRAINT [FK_Tasks_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("content", "Tasks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("content", "Tasks");

            migrationBuilder.DropTable(
                name: "Tasks",
                schema: "content");
        }
    }
}
