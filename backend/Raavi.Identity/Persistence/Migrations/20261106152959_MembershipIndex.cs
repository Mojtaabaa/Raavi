using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raavi.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MembershipIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserTenantMemberships",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTenantMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserTenantMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserTenantMemberships_UserId_TenantId",
                schema: "identity",
                table: "UserTenantMemberships",
                columns: new[] { "UserId", "TenantId" },
                unique: true);

            migrationBuilder.Sql("ALTER TABLE [identity].[UserTenantMemberships] ADD CONSTRAINT [FK_UserTenantMemberships_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");

            // Backfill from the RLS-protected memberships. The migrator has no tenant context, so the policy is switched
            // off only inside this migration's transaction (the runtime role cannot do this — see 01-database-roles.sql).
            migrationBuilder.Sql("ALTER SECURITY POLICY security.TenantIsolation WITH (STATE = OFF);");
            migrationBuilder.Sql("""
                INSERT INTO [identity].[UserTenantMemberships] (Id, UserId, TenantId, Status)
                SELECT NEWID(), m.UserId, m.TenantId, m.Status
                FROM [identity].[TenantMembers] m
                WHERE NOT EXISTS (SELECT 1 FROM [identity].[UserTenantMemberships] i WHERE i.UserId = m.UserId AND i.TenantId = m.TenantId);
                """);
            migrationBuilder.Sql("ALTER SECURITY POLICY security.TenantIsolation WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserTenantMemberships",
                schema: "identity");
        }
    }
}
