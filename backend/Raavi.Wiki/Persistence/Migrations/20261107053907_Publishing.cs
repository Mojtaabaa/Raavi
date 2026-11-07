using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Publishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequirePublishApproval",
                schema: "spaces",
                table: "Spaces",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RestoredFromVersionNumber",
                schema: "content",
                table: "PageVersions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublishedAt",
                schema: "content",
                table: "Content",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PageDrafts",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    BaseVersionNumber = table.Column<int>(type: "int", nullable: false),
                    Contributors = table.Column<string>(type: "varchar(4000)", unicode: false, maxLength: 4000, nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ScheduledPublishAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ScheduledBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ScheduledChangeSummary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageDrafts", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_PageDrafts_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_PageDrafts_Content_TenantId_ContentId",
                        columns: x => new { x.TenantId, x.ContentId },
                        principalSchema: "content",
                        principalTable: "Content",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PageVersionContributors",
                schema: "content",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageVersionContributors", x => new { x.TenantId, x.VersionId, x.UserId })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_PageVersionContributors_PageVersions_TenantId_VersionId",
                        columns: x => new { x.TenantId, x.VersionId },
                        principalSchema: "content",
                        principalTable: "PageVersions",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PublishRequests",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ChangeSummary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Contributors = table.Column<string>(type: "varchar(4000)", unicode: false, maxLength: 4000, nullable: false),
                    BaseVersionNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecisionComment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishRequests", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_PublishRequests_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_PublishRequests_Content_TenantId_ContentId",
                        columns: x => new { x.TenantId, x.ContentId },
                        principalSchema: "content",
                        principalTable: "Content",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PageDrafts_TenantId_ContentId",
                schema: "content",
                table: "PageDrafts",
                columns: new[] { "TenantId", "ContentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishRequests_TenantId_ContentId_Status",
                schema: "content",
                table: "PublishRequests",
                columns: new[] { "TenantId", "ContentId", "Status" });

            migrationBuilder.Sql("ALTER TABLE [content].[PageDrafts] ADD CONSTRAINT [FK_PageDrafts_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [content].[PageVersionContributors] ADD CONSTRAINT [FK_PageVersionContributors_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [content].[PublishRequests] ADD CONSTRAINT [FK_PublishRequests_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("content", "PageDrafts");
            migrationBuilder.AddTenantIsolation("content", "PageVersionContributors");
            migrationBuilder.AddTenantIsolation("content", "PublishRequests");

            // Backfill for existing data (Phase 5 upgrade). The migrator has no tenant context, so the policy is switched
            // off only inside this migration's transaction (the runtime role cannot do this — 01-database-roles.sql).
            migrationBuilder.Sql("ALTER SECURITY POLICY security.TenantIsolation WITH (STATE = OFF);");
            // Publishing became its own right: whoever could edit a space keeps being able to publish in it.
            migrationBuilder.Sql("""
                INSERT INTO [security].[PermissionGrants] (Id, TenantId, Scope, SpaceId, SubjectType, SubjectId, Action, Effect)
                SELECT NEWID(), g.TenantId, g.Scope, g.SpaceId, g.SubjectType, g.SubjectId, 5, g.Effect
                FROM [security].[PermissionGrants] g
                WHERE g.Action = 3 AND g.Scope = 3
                  AND NOT EXISTS (SELECT 1 FROM [security].[PermissionGrants] x
                                  WHERE x.TenantId = g.TenantId AND x.SpaceId = g.SpaceId AND x.SubjectType = g.SubjectType
                                    AND ((x.SubjectId IS NULL AND g.SubjectId IS NULL) OR x.SubjectId = g.SubjectId)
                                    AND x.Action = 5 AND x.Effect = g.Effect);
                """);
            migrationBuilder.Sql("""
                UPDATE c SET PublishedAt = v.CreatedAt
                FROM [content].[Content] c JOIN [content].[PageVersions] v ON v.TenantId = c.TenantId AND v.Id = c.CurrentVersionId
                WHERE c.PublishedAt IS NULL;
                """);
            migrationBuilder.Sql("""
                INSERT INTO [content].[PageVersionContributors] (TenantId, VersionId, UserId)
                SELECT v.TenantId, v.Id, v.CreatedBy FROM [content].[PageVersions] v
                WHERE v.CreatedBy IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM [content].[PageVersionContributors] x WHERE x.TenantId = v.TenantId AND x.VersionId = v.Id AND x.UserId = v.CreatedBy);
                """);
            migrationBuilder.Sql("ALTER SECURITY POLICY security.TenantIsolation WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("content", "PageDrafts");
            migrationBuilder.DropTenantIsolation("content", "PageVersionContributors");
            migrationBuilder.DropTenantIsolation("content", "PublishRequests");

            migrationBuilder.DropTable(
                name: "PageDrafts",
                schema: "content");

            migrationBuilder.DropTable(
                name: "PageVersionContributors",
                schema: "content");

            migrationBuilder.DropTable(
                name: "PublishRequests",
                schema: "content");

            migrationBuilder.DropColumn(
                name: "RequirePublishApproval",
                schema: "spaces",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "RestoredFromVersionNumber",
                schema: "content",
                table: "PageVersions");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                schema: "content",
                table: "Content");
        }
    }
}
