@echo off
REM Upgrade of THIS server (C:\inetpub\Raavi, pool RaaviAPI). Run as Administrator.
REM Usage:  Update-Raavi-Server.cmd C:\Deploy\raavi-0.18.0
REM Add  -Service RaaviWorkers  to the powershell line once the workers service is installed.
if "%~1"=="" (
  echo Usage: %~nx0 ^<extracted package folder^>   e.g. C:\Deploy\raavi-0.18.0
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-Raavi.ps1" -PackageDirectory "%~1" -InstallDirectory "C:\inetpub\Raavi" -AppPool RaaviAPI -RunMigrator
echo.
pause
