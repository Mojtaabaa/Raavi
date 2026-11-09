using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raavi.Wiki.Persistence.Migrations
{
    /// <summary>
    /// Page bodies are stored gzip-compressed (see GzipText). Data-preserving: the old text column is compressed into a new one with SQL Server's
    /// COMPRESS() (the same bytes the application writes), checked, then replaced; the table is rebuilt to give the freed space back.
    /// Row-Level Security has no bypass for tenant scopes (ADR-005) and the migration connection cannot set a tenant, so the policy is switched
    /// OFF for the duration of this one transaction (nobody else is connected during an upgrade: the update script stops the application first)
    /// and ON again before it commits; a failure anywhere rolls back everything, including the policy state.
    /// </summary>
    public partial class CompressPageVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE [content].[PageVersions] ADD [ContentJsonZ] varbinary(max) NULL;");
            migrationBuilder.Sql("ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = OFF);");
            migrationBuilder.Sql("UPDATE [content].[PageVersions] SET [ContentJsonZ] = ISNULL(COMPRESS([ContentJson]), 0x);");
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [content].[PageVersions] WHERE [ContentJsonZ] IS NULL) THROW 50000, 'PageVersions: not every body could be compressed; nothing was changed.', 1;");
            migrationBuilder.Sql("ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = ON);");
            migrationBuilder.Sql("ALTER TABLE [content].[PageVersions] DROP COLUMN [ContentJson];");
            migrationBuilder.Sql("EXEC sp_rename 'content.PageVersions.ContentJsonZ', 'ContentJson', 'COLUMN';");
            migrationBuilder.Sql("ALTER TABLE [content].[PageVersions] ALTER COLUMN [ContentJson] varbinary(max) NOT NULL;");
            migrationBuilder.Sql("ALTER INDEX ALL ON [content].[PageVersions] REBUILD;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE [content].[PageVersions] ADD [ContentJsonT] nvarchar(max) NULL;");
            migrationBuilder.Sql("ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = OFF);");
            migrationBuilder.Sql("UPDATE [content].[PageVersions] SET [ContentJsonT] = CASE WHEN DATALENGTH([ContentJson]) = 0 THEN N'' ELSE CAST(DECOMPRESS([ContentJson]) AS nvarchar(max)) END;");
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [content].[PageVersions] WHERE [ContentJsonT] IS NULL) THROW 50000, 'PageVersions: not every body could be restored; nothing was changed.', 1;");
            migrationBuilder.Sql("ALTER SECURITY POLICY [security].[TenantIsolation] WITH (STATE = ON);");
            migrationBuilder.Sql("ALTER TABLE [content].[PageVersions] DROP COLUMN [ContentJson];");
            migrationBuilder.Sql("EXEC sp_rename 'content.PageVersions.ContentJsonT', 'ContentJson', 'COLUMN';");
            migrationBuilder.Sql("ALTER TABLE [content].[PageVersions] ALTER COLUMN [ContentJson] nvarchar(max) NOT NULL;");
        }
    }
}
