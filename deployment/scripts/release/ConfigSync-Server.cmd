@echo off
REM After you copy the new version into C:\inetpub\Raavi (services already stopped), sync config + migrate.
REM Usage:  ConfigSync-Server.cmd
REM Set RAAVI_ConnectionStrings__Migrator in this window first, or rely on RAAVI_ConnectionStrings__System.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ConfigSync.ps1" -InstallDirectory "C:\inetpub\Raavi"
echo.
pause
