using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskTimeActiveIndexB6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TaskTimeEntries_TenantId_TaskId_Active",
                schema: "content",
                table: "TaskTimeEntries",
                columns: new[] { "TenantId", "TaskId" },
                filter: "[DeletedAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskTimeEntries_TenantId_TaskId_Active",
                schema: "content",
                table: "TaskTimeEntries");
        }
    }
}
