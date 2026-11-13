using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Raavi.Infrastructure.Persistence;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskCollaborationB4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TaskId",
                schema: "content",
                table: "Activity",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TaskComments",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BodyJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Excerpt = table.Column<string>(type: "nvarchar(280)", maxLength: 280, nullable: false),
                    AuthorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EditedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskComments", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_TaskComments_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_TaskComments_TaskComments_TenantId_ParentId",
                        columns: x => new { x.TenantId, x.ParentId },
                        principalSchema: "content",
                        principalTable: "TaskComments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskComments_Tasks_TenantId_TaskId",
                        columns: x => new { x.TenantId, x.TaskId },
                        principalSchema: "content",
                        principalTable: "Tasks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskWatchers",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskWatchers", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_TaskWatchers_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_TaskWatchers_Tasks_TenantId_TaskId",
                        columns: x => new { x.TenantId, x.TaskId },
                        principalSchema: "content",
                        principalTable: "Tasks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskAttachments",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    MediaType = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Data = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskAttachments", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_TaskAttachments_TenantId_Id", x => new { x.TenantId, x.Id })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_TaskAttachments_TaskComments_TenantId_CommentId",
                        columns: x => new { x.TenantId, x.CommentId },
                        principalSchema: "content",
                        principalTable: "TaskComments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskAttachments_Tasks_TenantId_TaskId",
                        columns: x => new { x.TenantId, x.TaskId },
                        principalSchema: "content",
                        principalTable: "Tasks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activity_TenantId_TaskId_OccurredAt",
                schema: "content",
                table: "Activity",
                columns: new[] { "TenantId", "TaskId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskAttachments_TenantId_CommentId",
                schema: "content",
                table: "TaskAttachments",
                columns: new[] { "TenantId", "CommentId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskAttachments_TenantId_TaskId_DeletedAt",
                schema: "content",
                table: "TaskAttachments",
                columns: new[] { "TenantId", "TaskId", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskComments_TenantId_ParentId",
                schema: "content",
                table: "TaskComments",
                columns: new[] { "TenantId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskComments_TenantId_TaskId_CreatedAt",
                schema: "content",
                table: "TaskComments",
                columns: new[] { "TenantId", "TaskId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskComments_TenantId_ThreadId_CreatedAt",
                schema: "content",
                table: "TaskComments",
                columns: new[] { "TenantId", "ThreadId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskWatchers_TenantId_TaskId_UserId",
                schema: "content",
                table: "TaskWatchers",
                columns: new[] { "TenantId", "TaskId", "UserId" },
                unique: true);

            // raavi:rls-patched — tenant FKs and Row-Level Security (TENANT_ISOLATION.md)
            migrationBuilder.Sql("ALTER TABLE [content].[TaskComments] ADD CONSTRAINT [FK_TaskComments_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [content].[TaskAttachments] ADD CONSTRAINT [FK_TaskAttachments_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.Sql("ALTER TABLE [content].[TaskWatchers] ADD CONSTRAINT [FK_TaskWatchers_SystemTenants] FOREIGN KEY ([TenantId]) REFERENCES [system].[Tenants] ([Id]);");
            migrationBuilder.AddTenantIsolation("content", "TaskComments");
            migrationBuilder.AddTenantIsolation("content", "TaskAttachments");
            migrationBuilder.AddTenantIsolation("content", "TaskWatchers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantIsolation("content", "TaskAttachments");
            migrationBuilder.DropTenantIsolation("content", "TaskWatchers");
            migrationBuilder.DropTenantIsolation("content", "TaskComments");

            migrationBuilder.DropTable(
                name: "TaskAttachments",
                schema: "content");

            migrationBuilder.DropTable(
                name: "TaskWatchers",
                schema: "content");

            migrationBuilder.DropTable(
                name: "TaskComments",
                schema: "content");

            migrationBuilder.DropIndex(
                name: "IX_Activity_TenantId_TaskId_OccurredAt",
                schema: "content",
                table: "Activity");

            migrationBuilder.DropColumn(
                name: "TaskId",
                schema: "content",
                table: "Activity");
        }
    }
}
