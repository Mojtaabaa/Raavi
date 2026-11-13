using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskAssigneesPrimaryUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignees_TenantId_TaskId",
                schema: "content",
                table: "TaskAssignees",
                columns: new[] { "TenantId", "TaskId" },
                unique: true,
                filter: "[RemovedAt] IS NULL AND [IsPrimary] = CAST(1 AS bit)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskAssignees_TenantId_TaskId",
                schema: "content",
                table: "TaskAssignees");
        }
    }
}
