using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskTimeEntriesB5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskTimeEntries",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsOpeningBalance = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTimeEntries", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_TaskTimeEntries_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_TaskTimeEntries_Tasks_TenantId_TaskId",
                        columns: x => new { x.TenantId, x.TaskId },
                        principalSchema: "content",
                        principalTable: "Tasks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskTimeEntries_TenantId_TaskId_WorkDate",
                schema: "content",
                table: "TaskTimeEntries",
                columns: new[] { "TenantId", "TaskId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskTimeEntries_TenantId_UserId_WorkDate",
                schema: "content",
                table: "TaskTimeEntries",
                columns: new[] { "TenantId", "UserId", "WorkDate" });

            // raavi:rls-patched — tenant FKs and Row-Level Security (TENANT_ISOLATION.md)
            migrationBuilder.Sql("ALTER TABLE [content].[TaskTimeEntries] ADD CONSTRAINT [FK_TaskTimeEntries_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("content", "TaskTimeEntries");

            // Preserve legacy ActualSpentMinutes as opening-balance entries (migrator has no tenant SESSION_CONTEXT).
            migrationBuilder.Sql("ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = OFF);");
            migrationBuilder.Sql("""
                INSERT INTO [content].[TaskTimeEntries]
                    ([Id], [TenantId], [TaskId], [UserId], [WorkDate], [DurationMinutes], [Description], [IsOpeningBalance],
                     [CreatedBy], [CreatedAt], [UpdatedAt], [DeletedAt], [DeletedBy], [Revision])
                SELECT NEWID(), t.[TenantId], t.[Id], t.[CreatedBy], CAST(t.[CreatedAt] AS date), t.[ActualSpentMinutes],
                       N'Opening balance (pre–time-tracking)', CAST(1 AS bit),
                       t.[CreatedBy], t.[CreatedAt], t.[CreatedAt], NULL, NULL, 1
                FROM [content].[Tasks] t
                WHERE t.[ActualSpentMinutes] IS NOT NULL AND t.[ActualSpentMinutes] > 0
                  AND NOT EXISTS (
                      SELECT 1 FROM [content].[TaskTimeEntries] e
                      WHERE e.[TenantId] = t.[TenantId] AND e.[TaskId] = t.[Id] AND e.[IsOpeningBalance] = CAST(1 AS bit));
                """);
            migrationBuilder.Sql("ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("content", "TaskTimeEntries");
            migrationBuilder.DropTable(
                name: "TaskTimeEntries",
                schema: "content");
        }
    }
}
