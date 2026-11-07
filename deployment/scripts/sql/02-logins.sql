/*
  Raavi — server logins and database users. Run with sqlcmd in SQLCMD mode, e.g.:

    sqlcmd -S <server> -E -i 02-logins.sql -v DatabaseName="Raavi" AppPassword="<from secret store>" MigratorPassword="<from secret store>"

  Passwords are passed as sqlcmd variables from your secret store — never commit them.
  Windows/AD logins (gMSA) can be used instead of SQL logins: replace CREATE LOGIN ... WITH PASSWORD by FROM WINDOWS.
  Run 01-database-roles.sql first.
*/
SET NOCOUNT ON;

USE [master];
IF SUSER_ID(N'raavi_app') IS NULL
    CREATE LOGIN [raavi_app] WITH PASSWORD = N'$(AppPassword)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [$(DatabaseName)];
IF SUSER_ID(N'raavi_migrator') IS NULL
    CREATE LOGIN [raavi_migrator] WITH PASSWORD = N'$(MigratorPassword)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [$(DatabaseName)];

USE [$(DatabaseName)];
IF DATABASE_PRINCIPAL_ID(N'raavi_app') IS NULL
    CREATE USER [raavi_app] FOR LOGIN [raavi_app] WITH DEFAULT_SCHEMA = [system];
IF DATABASE_PRINCIPAL_ID(N'raavi_migrator') IS NULL
    CREATE USER [raavi_migrator] FOR LOGIN [raavi_migrator] WITH DEFAULT_SCHEMA = [dbo];

ALTER ROLE raavi_app_role ADD MEMBER [raavi_app];
ALTER ROLE db_owner ADD MEMBER [raavi_migrator];

/*
  Connection strings:
    API / Workers : RAAVI_ConnectionStrings__System   = ...;User Id=raavi_app;...
    Migrator only : RAAVI_ConnectionStrings__Migrator = ...;User Id=raavi_migrator;...
*/
