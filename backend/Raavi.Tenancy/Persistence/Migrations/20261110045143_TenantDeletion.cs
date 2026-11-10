using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raavi.Tenancy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "system",
                table: "Tenants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletionRequestedAt",
                schema: "system",
                table: "Tenants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionRequestedBy",
                schema: "system",
                table: "Tenants",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletionScheduledFor",
                schema: "system",
                table: "Tenants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatusBeforeDeletion",
                schema: "system",
                table: "Tenants",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "system",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "DeletionRequestedAt",
                schema: "system",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "DeletionRequestedBy",
                schema: "system",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "DeletionScheduledFor",
                schema: "system",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "StatusBeforeDeletion",
                schema: "system",
                table: "Tenants");
        }
    }
}
