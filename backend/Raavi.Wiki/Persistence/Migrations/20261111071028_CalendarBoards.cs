using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <summary>
    /// Stage A calendar containers: Calendars + access grants; every existing space event is linked to a default Space calendar.
    /// Data-preserving and idempotent backfill. RLS is switched OFF for the backfill (migrator cannot set a tenant) and ON again
    /// before commit — same pattern as CompressPageVersions.
    /// </summary>
    public partial class CalendarBoards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Calendars",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Color = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Icon = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Calendars", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_Calendars_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_Calendars_Spaces_TenantId_SpaceId",
                        columns: x => new { x.TenantId, x.SpaceId },
                        principalSchema: "spaces",
                        principalTable: "Spaces",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CalendarAccessGrants",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectType = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Grant = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarAccessGrants", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_CalendarAccessGrants_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_CalendarAccessGrants_Calendars_TenantId_CalendarId",
                        columns: x => new { x.TenantId, x.CalendarId },
                        principalSchema: "content",
                        principalTable: "Calendars",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddColumn<Guid>(
                name: "CalendarId",
                schema: "content",
                table: "CalendarEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Color",
                schema: "content",
                table: "CalendarEvents",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPrivate",
                schema: "content",
                table: "CalendarEvents",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OnlineUrl",
                schema: "content",
                table: "CalendarEvents",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizerId",
                schema: "content",
                table: "CalendarEvents",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                schema: "content",
                table: "CalendarEvents",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: false,
                defaultValue: "Normal");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "content",
                table: "CalendarEvents",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: false,
                defaultValue: "Scheduled");

            migrationBuilder.AddColumn<string>(
                name: "Type",
                schema: "content",
                table: "CalendarEvents",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: false,
                defaultValue: "Event");

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedBy",
                schema: "content",
                table: "CalendarEvents",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AlterColumn<Guid>(
                name: "SpaceId",
                schema: "content",
                table: "CalendarEvents",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            // raavi:rls-patched — tenant FKs and Row-Level Security (TENANT_ISOLATION.md)
            migrationBuilder.Sql("ALTER TABLE [content].[Calendars] ADD CONSTRAINT [FK_Calendars_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [content].[CalendarAccessGrants] ADD CONSTRAINT [FK_CalendarAccessGrants_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("content", "Calendars");
            migrationBuilder.AddTenantIsolation("content", "CalendarAccessGrants");

            // Backfill: one default Space calendar per space that already has events; link every event; preserve counts.
            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = OFF);
                DECLARE @before int = (SELECT COUNT(*) FROM [content].[CalendarEvents]);
                INSERT INTO [content].[Calendars] (
                    [Id], [TenantId], [Scope], [SpaceId], [OwnerUserId], [Name], [Description], [Color], [Icon],
                    [IsDefault], [ArchivedAt], [CreatedBy], [UpdatedBy], [CreatedAt], [UpdatedAt], [Revision])
                SELECT
                    NEWID(), s.[TenantId], 'Space', s.[Id], NULL, s.[Name], NULL, '#3b82f6', NULL,
                    1, NULL, s.[CreatedBy], s.[CreatedBy], SYSUTCDATETIME(), SYSUTCDATETIME(), 1
                FROM [spaces].[Spaces] s
                WHERE EXISTS (
                    SELECT 1 FROM [content].[CalendarEvents] e
                    WHERE e.[TenantId] = s.[TenantId] AND e.[SpaceId] = s.[Id])
                AND NOT EXISTS (
                    SELECT 1 FROM [content].[Calendars] c
                    WHERE c.[TenantId] = s.[TenantId] AND c.[SpaceId] = s.[Id] AND c.[Scope] = 'Space' AND c.[IsDefault] = 1);
                UPDATE e
                SET
                    e.[CalendarId] = c.[Id],
                    e.[OrganizerId] = CASE WHEN e.[OrganizerId] = '00000000-0000-0000-0000-000000000000' THEN e.[CreatedBy] ELSE e.[OrganizerId] END,
                    e.[UpdatedBy] = CASE WHEN e.[UpdatedBy] = '00000000-0000-0000-0000-000000000000' THEN e.[CreatedBy] ELSE e.[UpdatedBy] END,
                    e.[Type] = CASE WHEN e.[Type] = N'' THEN N'Event' ELSE e.[Type] END,
                    e.[Status] = CASE WHEN e.[Status] = N'' THEN N'Scheduled' ELSE e.[Status] END,
                    e.[Priority] = CASE WHEN e.[Priority] = N'' THEN N'Normal' ELSE e.[Priority] END
                FROM [content].[CalendarEvents] e
                INNER JOIN [content].[Calendars] c
                    ON c.[TenantId] = e.[TenantId] AND c.[SpaceId] = e.[SpaceId] AND c.[Scope] = 'Space' AND c.[IsDefault] = 1
                WHERE e.[CalendarId] IS NULL;
                IF EXISTS (SELECT 1 FROM [content].[CalendarEvents] WHERE [CalendarId] IS NULL)
                    THROW 50001, 'CalendarBoards: every event must be linked to a calendar after backfill.', 1;
                IF @before <> (SELECT COUNT(*) FROM [content].[CalendarEvents])
                    THROW 50002, 'CalendarBoards: event count changed during backfill.', 1;
                ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = ON);
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "CalendarId",
                schema: "content",
                table: "CalendarEvents",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_TenantId_CalendarId_StartDate",
                schema: "content",
                table: "CalendarEvents",
                columns: new[] { "TenantId", "CalendarId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarAccessGrants_TenantId_CalendarId_SubjectType_SubjectId_Grant",
                schema: "content",
                table: "CalendarAccessGrants",
                columns: new[] { "TenantId", "CalendarId", "SubjectType", "SubjectId", "Grant" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Calendars_TenantId_Scope_SpaceId_OwnerUserId",
                schema: "content",
                table: "Calendars",
                columns: new[] { "TenantId", "Scope", "SpaceId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Calendars_TenantId_SpaceId_IsDefault",
                schema: "content",
                table: "Calendars",
                columns: new[] { "TenantId", "SpaceId", "IsDefault" });

            migrationBuilder.AddForeignKey(
                name: "FK_CalendarEvents_Calendars_TenantId_CalendarId",
                schema: "content",
                table: "CalendarEvents",
                columns: new[] { "TenantId", "CalendarId" },
                principalSchema: "content",
                principalTable: "Calendars",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CalendarEvents_Calendars_TenantId_CalendarId",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.DropTenantIsolation("content", "CalendarAccessGrants");
            migrationBuilder.DropTenantIsolation("content", "Calendars");

            migrationBuilder.DropTable(
                name: "CalendarAccessGrants",
                schema: "content");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_TenantId_CalendarId_StartDate",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = OFF);
                UPDATE e SET e.[SpaceId] = c.[SpaceId]
                FROM [content].[CalendarEvents] e
                INNER JOIN [content].[Calendars] c ON c.[TenantId] = e.[TenantId] AND c.[Id] = e.[CalendarId]
                WHERE e.[SpaceId] IS NULL AND c.[SpaceId] IS NOT NULL;
                IF EXISTS (SELECT 1 FROM [content].[CalendarEvents] WHERE [SpaceId] IS NULL)
                    THROW 50003, 'CalendarBoards Down: cannot restore SpaceId for every event.', 1;
                ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = ON);
                """);

            migrationBuilder.DropColumn(
                name: "CalendarId",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "Color",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "IsPrivate",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "OnlineUrl",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "OrganizerId",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "Priority",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "Type",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                schema: "content",
                table: "CalendarEvents");

            migrationBuilder.DropTable(
                name: "Calendars",
                schema: "content");

            migrationBuilder.AlterColumn<Guid>(
                name: "SpaceId",
                schema: "content",
                table: "CalendarEvents",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
