using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CalendarMeetings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LinkedPageId",
                schema: "content",
                table: "CalendarEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LinkedWhiteboardId",
                schema: "content",
                table: "CalendarEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecurMonthWeek",
                schema: "content",
                table: "CalendarEvents",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CalendarEventAttendees",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Response = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Optional = table.Column<bool>(type: "bit", nullable: false),
                    InvitedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEventAttendees", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_CalendarEventAttendees_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_CalendarEventAttendees_CalendarEvents_TenantId_EventId",
                        columns: x => new { x.TenantId, x.EventId },
                        principalSchema: "content",
                        principalTable: "CalendarEvents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_TenantId_LinkedPageId",
                schema: "content",
                table: "CalendarEvents",
                columns: new[] { "TenantId", "LinkedPageId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_TenantId_LinkedWhiteboardId",
                schema: "content",
                table: "CalendarEvents",
                columns: new[] { "TenantId", "LinkedWhiteboardId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventAttendees_TenantId_EventId_UserId",
                schema: "content",
                table: "CalendarEventAttendees",
                columns: new[] { "TenantId", "EventId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventAttendees_TenantId_UserId_Response",
                schema: "content",
                table: "CalendarEventAttendees",
                columns: new[] { "TenantId", "UserId", "Response" });

            migrationBuilder.Sql("ALTER TABLE [content].[CalendarEventAttendees] ADD CONSTRAINT [FK_CalendarEventAttendees_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("content", "CalendarEventAttendees");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("content", "CalendarEventAttendees");
            migrationBuilder.DropTable(
                name: "CalendarEventAttendees",
                schema: "content");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_TenantId_LinkedPageId",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_TenantId_LinkedWhiteboardId",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "LinkedPageId",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "LinkedWhiteboardId",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "RecurMonthWeek",
                schema: "content",
                table: "CalendarEvents");
        }
    }
}
