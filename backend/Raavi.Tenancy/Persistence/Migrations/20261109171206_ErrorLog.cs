using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raavi.Tenancy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ErrorLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ErrorLog",
                schema: "system",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Source = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    TenantCode = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Method = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: true),
                    Path = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    StatusCode = table.Column<int>(type: "int", nullable: true),
                    ExceptionType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(1100)", maxLength: 1100, nullable: false),
                    StackTrace = table.Column<string>(type: "nvarchar(max)", maxLength: 6100, nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    Fingerprint = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorLog", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLog_Fingerprint_OccurredAt",
                schema: "system",
                table: "ErrorLog",
                columns: new[] { "Fingerprint", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLog_OccurredAt",
                schema: "system",
                table: "ErrorLog",
                column: "OccurredAt")
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ErrorLog",
                schema: "system");
        }
    }
}
