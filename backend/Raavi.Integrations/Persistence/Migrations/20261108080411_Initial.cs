using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.Integrations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "integrations");

            migrationBuilder.CreateTable(
                name: "Webhooks",
                schema: "integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ProtectedSecret = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    EventsCsv = table.Column<string>(type: "varchar(1200)", unicode: false, maxLength: 1200, nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    DisabledReason = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Webhooks", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_Webhooks_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                schema: "integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WebhookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_WebhookDeliveries_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_Webhooks_TenantId_WebhookId",
                        columns: x => new { x.TenantId, x.WebhookId },
                        principalSchema: "integrations",
                        principalTable: "Webhooks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_TenantId_WebhookId_At",
                schema: "integrations",
                table: "WebhookDeliveries",
                columns: new[] { "TenantId", "WebhookId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_TenantId_WebhookId_EventId",
                schema: "integrations",
                table: "WebhookDeliveries",
                columns: new[] { "TenantId", "WebhookId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_TenantId_Enabled",
                schema: "integrations",
                table: "Webhooks",
                columns: new[] { "TenantId", "Enabled" });

            // raavi:rls-patched — tenant FKs and Row-Level Security (TENANT_ISOLATION.md)
            migrationBuilder.Sql("ALTER TABLE [integrations].[Webhooks] ADD CONSTRAINT [FK_Webhooks_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [integrations].[WebhookDeliveries] ADD CONSTRAINT [FK_WebhookDeliveries_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("integrations", "Webhooks");
            migrationBuilder.AddTenantIsolation("integrations", "WebhookDeliveries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("integrations", "Webhooks");
            migrationBuilder.DropTenantIsolation("integrations", "WebhookDeliveries");

            migrationBuilder.DropTable(
                name: "WebhookDeliveries",
                schema: "integrations");

            migrationBuilder.DropTable(
                name: "Webhooks",
                schema: "integrations");
        }
    }
}
