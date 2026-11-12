using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskEnterpriseFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                schema: "content",
                table: "Tasks",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalEstimateMinutes",
                schema: "content",
                table: "Tasks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentTaskId",
                schema: "content",
                table: "Tasks",
                type: "uniqueidentifier",
                nullable: true);

            // WorkTaskPriority.Normal = 3 (existing rows must not land on 0).
            migrationBuilder.AddColumn<int>(
                name: "Priority",
                schema: "content",
                table: "Tasks",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "RemainingEstimateMinutes",
                schema: "content",
                table: "Tasks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "StartDate",
                schema: "content",
                table: "Tasks",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TaskAssignees",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    AssignedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskAssignees", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_TaskAssignees_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_TaskAssignees_Tasks_TenantId_TaskId",
                        columns: x => new { x.TenantId, x.TaskId },
                        principalSchema: "content",
                        principalTable: "Tasks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_TenantId_ParentTaskId",
                schema: "content",
                table: "Tasks",
                columns: new[] { "TenantId", "ParentTaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_TenantId_SpaceId_ArchivedAt",
                schema: "content",
                table: "Tasks",
                columns: new[] { "TenantId", "SpaceId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignees_TenantId_TaskId_UserId_Role",
                schema: "content",
                table: "TaskAssignees",
                columns: new[] { "TenantId", "TaskId", "UserId", "Role" },
                unique: true,
                filter: "[RemovedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignees_TenantId_UserId_RemovedAt",
                schema: "content",
                table: "TaskAssignees",
                columns: new[] { "TenantId", "UserId", "RemovedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_Tasks_TenantId_ParentTaskId",
                schema: "content",
                table: "Tasks",
                columns: new[] { "TenantId", "ParentTaskId" },
                principalSchema: "content",
                principalTable: "Tasks",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // raavi:rls-patched — tenant FKs and Row-Level Security (TENANT_ISOLATION.md)
            migrationBuilder.Sql("ALTER TABLE [content].[TaskAssignees] ADD CONSTRAINT [FK_TaskAssignees_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("content", "TaskAssignees");

            // Dual-read backfill: existing AssigneeId → primary Responsible (migrator has no tenant SESSION_CONTEXT).
            migrationBuilder.Sql("ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = OFF);");
            migrationBuilder.Sql("""
                INSERT INTO [content].[TaskAssignees] ([Id], [TenantId], [TaskId], [UserId], [Role], [IsPrimary], [AssignedBy], [AssignedAt], [RemovedAt])
                SELECT NEWID(), t.[TenantId], t.[Id], t.[AssigneeId], 'Responsible', 1, t.[CreatedBy], t.[CreatedAt], NULL
                FROM [content].[Tasks] AS t
                WHERE t.[AssigneeId] IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM [content].[TaskAssignees] AS a
                    WHERE a.[TenantId] = t.[TenantId] AND a.[TaskId] = t.[Id] AND a.[UserId] = t.[AssigneeId]
                      AND a.[Role] = 'Responsible' AND a.[RemovedAt] IS NULL);
                """);
            migrationBuilder.Sql("ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("content", "TaskAssignees");

            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_Tasks_TenantId_ParentTaskId",
                schema: "content",
                table: "Tasks");

            migrationBuilder.DropTable(
                name: "TaskAssignees",
                schema: "content");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_TenantId_ParentTaskId",
                schema: "content",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_TenantId_SpaceId_ArchivedAt",
                schema: "content",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                schema: "content",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "OriginalEstimateMinutes",
                schema: "content",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ParentTaskId",
                schema: "content",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Priority",
                schema: "content",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "RemainingEstimateMinutes",
                schema: "content",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "StartDate",
                schema: "content",
                table: "Tasks");
        }
    }
}
