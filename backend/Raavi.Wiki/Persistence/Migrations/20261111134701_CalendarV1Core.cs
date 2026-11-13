using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CalendarV1Core : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalendarEventReminders",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MinutesBefore = table.Column<int>(type: "int", nullable: false),
                    InApp = table.Column<bool>(type: "bit", nullable: false),
                    Email = table.Column<bool>(type: "bit", nullable: false),
                    LastDeliveredOccurrence = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: true),
                    LastDeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEventReminders", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_CalendarEventReminders_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_CalendarEventReminders_CalendarEvents_TenantId_EventId",
                        columns: x => new { x.TenantId, x.EventId },
                        principalSchema: "content",
                        principalTable: "CalendarEvents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CalendarUserPreferences",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrowserAlerts = table.Column<bool>(type: "bit", nullable: false),
                    TtsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    TtsLocale = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    TtsRate = table.Column<double>(type: "float", nullable: false),
                    HidePrivateTitlesInTts = table.Column<bool>(type: "bit", nullable: false),
                    QuietStartMinutes = table.Column<int>(type: "int", nullable: true),
                    QuietEndMinutes = table.Column<int>(type: "int", nullable: true),
                    TimeZone = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarUserPreferences", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_CalendarUserPreferences_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventReminders_TenantId_EventId_MinutesBefore",
                schema: "content",
                table: "CalendarEventReminders",
                columns: new[] { "TenantId", "EventId", "MinutesBefore" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarUserPreferences_TenantId_UserId",
                schema: "content",
                table: "CalendarUserPreferences",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.Sql("ALTER TABLE [content].[CalendarEventReminders] ADD CONSTRAINT [FK_CalendarEventReminders_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [content].[CalendarUserPreferences] ADD CONSTRAINT [FK_CalendarUserPreferences_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("content", "CalendarEventReminders");
            migrationBuilder.AddTenantIsolation("content", "CalendarUserPreferences");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("content", "CalendarEventReminders");
            migrationBuilder.DropTenantIsolation("content", "CalendarUserPreferences");
            migrationBuilder.DropTable(
                name: "CalendarEventReminders",
                schema: "content");

            migrationBuilder.DropTable(
                name: "CalendarUserPreferences",
                schema: "content");
        }
    }
}
