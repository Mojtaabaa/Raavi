/*
  Raavi — database roles (run in the Raavi database AFTER Raavi.Migrator has created the schemas).
  Idempotent. Covered by Raavi.Tests/Isolation/DatabaseRolesTests.cs.

  raavi_app_role   : application runtime (Raavi.Api, Raavi.Workers). DML only on Raavi schemas; cannot change schema
                     objects or the Row-Level Security policy. RLS applies to it like to every other principal.
  raavi_migrator   : use a separate login that is db_owner (or db_ddladmin + ALTER ANY SECURITY POLICY) — only for
                     Raavi.Migrator. Never configure it in the API/Workers connection strings.
*/
SET NOCOUNT ON;

IF DATABASE_PRINCIPAL_ID(N'raavi_app_role') IS NULL
    CREATE ROLE raavi_app_role AUTHORIZATION dbo;

DECLARE @schema sysname, @sql nvarchar(400);
DECLARE schemas CURSOR LOCAL FAST_FORWARD FOR
    SELECT name FROM sys.schemas WHERE name IN (N'system', N'audit', N'identity', N'hangfire');
OPEN schemas;
FETCH NEXT FROM schemas INTO @schema;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::' + QUOTENAME(@schema) + N' TO raavi_app_role;';
    EXEC sys.sp_executesql @sql;
    FETCH NEXT FROM schemas INTO @schema;
END
CLOSE schemas;
DEALLOCATE schemas;

-- Hangfire SQL storage uses its own tables only (schema installed by the migrator, PrepareSchemaIfNecessary = false).

-- Defence in depth: the runtime role can never alter structures, security policies or the predicate schema.
DENY ALTER ON SCHEMA::[security] TO raavi_app_role;
DENY CONTROL ON SCHEMA::[security] TO raavi_app_role;
DENY ALTER ANY SECURITY POLICY TO raavi_app_role;
DENY ALTER ANY SCHEMA TO raavi_app_role;
DENY CREATE TABLE TO raavi_app_role;
DENY CREATE FUNCTION TO raavi_app_role;
DENY CREATE PROCEDURE TO raavi_app_role;
DENY ALTER ON SCHEMA::[system] TO raavi_app_role;
DENY ALTER ON SCHEMA::[identity] TO raavi_app_role;
DENY ALTER ON SCHEMA::[audit] TO raavi_app_role;
