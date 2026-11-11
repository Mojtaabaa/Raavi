<#
.SYNOPSIS
  Syncs installation sandbox settings from sandbox-defaults and optionally runs the database migrator.
.DESCRIPTION
  Operator copies application folders and stops/starts services separately.
  This script only:
  1. ensures sandbox/{config,data,secrets} exist (first install: copies config from sandbox-defaults when empty);
  2. runs `Raavi.Migrator sandbox sync` (adds settings new in this version; never changes existing values; originals go to sandbox/backups);
  3. unless -SkipMigrator, applies database migrations (needs RAAVI_ConnectionStrings__Migrator or __System in the environment).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$InstallDirectory,
    [switch]$SkipMigrator
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$install = (Resolve-Path -LiteralPath $InstallDirectory).Path
$migratorDll = Join-Path $install 'migrator/Raavi.Migrator.dll'
if (-not (Test-Path -LiteralPath $migratorDll)) { throw "Migrator not found (copy application folders first): $migratorDll" }
$defaults = Join-Path $install 'sandbox-defaults/config'
if (-not (Test-Path -LiteralPath (Join-Path $defaults 'appsettings.json'))) {
    throw "sandbox-defaults/config missing under $install (copy application folders first)."
}

$sandbox = Join-Path $install 'sandbox'
foreach ($sub in @('config', 'data', 'secrets')) {
    $dir = Join-Path $sandbox $sub
    if (-not (Test-Path -LiteralPath $dir)) {
        if ($PSCmdlet.ShouldProcess($dir, 'create')) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    }
}

$sandboxConfig = Join-Path $sandbox 'config'
if (-not (Test-Path -LiteralPath (Join-Path $sandboxConfig 'appsettings.json')) -and $PSCmdlet.ShouldProcess($sandboxConfig, 'initialise from sandbox-defaults')) {
    Write-Host '== first install: sandbox/config initialised from sandbox-defaults'
    Copy-Item -Recurse -Force -Path (Join-Path $defaults '*') -Destination $sandboxConfig
}

if ($PSCmdlet.ShouldProcess($sandboxConfig, 'sandbox sync')) {
    Write-Host '== sandbox sync'
    $env:RAAVI_SANDBOX_PATH = $sandboxConfig
    $env:RAAVI_SANDBOX_DEFAULTS_PATH = $defaults
    & dotnet $migratorDll sandbox sync
    if ($LASTEXITCODE -ne 0) { throw "sandbox sync failed (exit $LASTEXITCODE)." }
}

if (-not $SkipMigrator -and $PSCmdlet.ShouldProcess('database', 'apply migrations')) {
    Write-Host '== database migrations'
    & dotnet $migratorDll
    if ($LASTEXITCODE -ne 0) { throw "Migrator failed (exit $LASTEXITCODE). Restore the database from backup if migrations partially ran." }
}

Write-Host "Done: config synced under $install.$(if ($SkipMigrator) { ' Database migrator skipped.' } else { ' Database migrator applied.' }) Start services yourself."
