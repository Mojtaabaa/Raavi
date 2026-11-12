using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskEnterpriseB1Compatibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskAssignees_TenantId_TaskId_UserId_Role",
                schema: "content",
                table: "TaskAssignees");

            migrationBuilder.AddColumn<int>(
                name: "ActualSpentMinutes",
                schema: "content",
                table: "Tasks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BlockCompleteWhenOpenChildren",
                schema: "content",
                table: "Tasks",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<byte>(
                name: "ProgressPercent",
                schema: "content",
                table: "Tasks",
                type: "tinyint",
                nullable: true);

            // Abort (no silent deletes) if a user already has multiple active roles on one task.
            migrationBuilder.Sql("ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = OFF);");
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1
                    FROM [content].[TaskAssignees]
                    WHERE [RemovedAt] IS NULL
                    GROUP BY [TenantId], [TaskId], [UserId]
                    HAVING COUNT(*) > 1)
                    THROW 50001, 'TaskAssignees: duplicate active users per task; fix data then re-run. No rows were deleted.', 1;
                """);
            migrationBuilder.Sql("ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = ON);");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignees_TenantId_TaskId_UserId",
                schema: "content",
                table: "TaskAssignees",
                columns: new[] { "TenantId", "TaskId", "UserId" },
                unique: true,
                filter: "[RemovedAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskAssignees_TenantId_TaskId_UserId",
                schema: "content",
                table: "TaskAssignees");

            migrationBuilder.DropColumn(
                name: "ActualSpentMinutes",
                schema: "content",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "BlockCompleteWhenOpenChildren",
                schema: "content",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ProgressPercent",
                schema: "content",
                table: "Tasks");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignees_TenantId_TaskId_UserId_Role",
                schema: "content",
                table: "TaskAssignees",
                columns: new[] { "TenantId", "TaskId", "UserId", "Role" },
                unique: true,
                filter: "[RemovedAt] IS NULL");
        }
    }
}
