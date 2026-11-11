using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.Integrations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PageIssueLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Outbox",
                schema: "integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "PageIssueLinks",
                schema: "integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssueId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    IssueKey = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IssueType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Priority = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Assignee = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ProjectKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    IssueUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Source = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    StatusFlag = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    SyncState = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    RemoteLinkGlobalId = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    RemoteLinkId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSyncError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SyncAttempts = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UnlinkedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UnlinkedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageIssueLinks", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_PageIssueLinks_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_PageIssueLinks_IssueConnectors_TenantId_ConnectorId",
                        columns: x => new { x.TenantId, x.ConnectorId },
                        principalSchema: "integrations",
                        principalTable: "IssueConnectors",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PageIssueLinkContexts",
                schema: "integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Exact = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Suffix = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AnchorJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageIssueLinkContexts", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_PageIssueLinkContexts_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_PageIssueLinkContexts_PageIssueLinks_TenantId_LinkId",
                        columns: x => new { x.TenantId, x.LinkId },
                        principalSchema: "integrations",
                        principalTable: "PageIssueLinks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_OccurredAt",
                schema: "integrations",
                table: "Outbox",
                column: "OccurredAt")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Status_AvailableAt",
                schema: "integrations",
                table: "Outbox",
                columns: new[] { "Status", "AvailableAt" })
                .Annotation("SqlServer:Clustered", false);

            migrationBuilder.CreateIndex(
                name: "IX_PageIssueLinkContexts_TenantId_LinkId",
                schema: "integrations",
                table: "PageIssueLinkContexts",
                columns: new[] { "TenantId", "LinkId" });

            migrationBuilder.CreateIndex(
                name: "IX_PageIssueLinks_TenantId_ConnectorId_IssueId_StatusFlag",
                schema: "integrations",
                table: "PageIssueLinks",
                columns: new[] { "TenantId", "ConnectorId", "IssueId", "StatusFlag" });

            migrationBuilder.CreateIndex(
                name: "IX_PageIssueLinks_TenantId_ContentId_ConnectorId_IssueId_StatusFlag",
                schema: "integrations",
                table: "PageIssueLinks",
                columns: new[] { "TenantId", "ContentId", "ConnectorId", "IssueId", "StatusFlag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PageIssueLinks_TenantId_ContentId_StatusFlag",
                schema: "integrations",
                table: "PageIssueLinks",
                columns: new[] { "TenantId", "ContentId", "StatusFlag" });

            migrationBuilder.CreateIndex(
                name: "IX_PageIssueLinks_TenantId_SyncState_UpdatedAt",
                schema: "integrations",
                table: "PageIssueLinks",
                columns: new[] { "TenantId", "SyncState", "UpdatedAt" });

            // raavi:rls-patched — tenant FKs and Row-Level Security (TENANT_ISOLATION.md)
            // Outbox has no RLS (same as content.Outbox): the dispatcher sets SESSION_CONTEXT per message.
            migrationBuilder.Sql("ALTER TABLE [integrations].[PageIssueLinks] ADD CONSTRAINT [FK_PageIssueLinks_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [integrations].[PageIssueLinkContexts] ADD CONSTRAINT [FK_PageIssueLinkContexts_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("integrations", "PageIssueLinks");
            migrationBuilder.AddTenantIsolation("integrations", "PageIssueLinkContexts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("integrations", "PageIssueLinkContexts");
            migrationBuilder.DropTenantIsolation("integrations", "PageIssueLinks");

            migrationBuilder.DropTable(
                name: "Outbox",
                schema: "integrations");

            migrationBuilder.DropTable(
                name: "PageIssueLinkContexts",
                schema: "integrations");

            migrationBuilder.DropTable(
                name: "PageIssueLinks",
                schema: "integrations");
        }
    }
}
