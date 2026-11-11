<#
.SYNOPSIS
  Self-test of New-ReleasePackage.ps1 and ConfigSync.ps1 in a throw-away folder (Phase 12d, ADR-041). Exit code 0 = all checks passed.
  Builds the backend for real (dotnet publish), skips the frontend build (use -WithFrontend to include it).
#>
[CmdletBinding()]
param([switch]$WithFrontend)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("raavi-release-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$script:passed = 0
$script:failed = 0
function Check([string]$Name, [bool]$Condition) {
    if ($Condition) { $script:passed++; Write-Host "  ok   $Name" } else { $script:failed++; Write-Host "  FAIL $Name" -ForegroundColor Red }
}
function Throws([scriptblock]$Action) { try { & $Action | Out-Null; return $false } catch { return $true } }

try {
    $version = 'selftest'
    Write-Host '== package'
    $args1 = @{ Version = $version; OutputDirectory = $work }
    if (-not $WithFrontend) { $args1.SkipFrontend = $true }
    & (Join-Path $PSScriptRoot 'New-ReleasePackage.ps1') @args1 | Out-Null
    $pkg = Join-Path $work "raavi-$version"
    Check 'package folder and zip exist' ((Test-Path $pkg) -and (Test-Path "$pkg.zip"))
    Check 'release.json lists files' (@((Get-Content (Join-Path $pkg 'release.json') -Raw | ConvertFrom-Json).files).Count -gt 100)
    Check 'api/ migrator/ workers/ published' ((Test-Path (Join-Path $pkg 'api/Raavi.Api.dll')) -and (Test-Path (Join-Path $pkg 'migrator/Raavi.Migrator.dll')) -and (Test-Path (Join-Path $pkg 'workers/Raavi.Workers.dll')))
    Check 'sandbox-defaults and collaboration shipped' ((Test-Path (Join-Path $pkg 'sandbox-defaults/config/appsettings.json')) -and (Test-Path (Join-Path $pkg 'collaboration/src/main.ts')))
    Check 'no sandbox / secrets / key ring in package' (-not (Get-ChildItem $pkg -Recurse -Force -Directory | Where-Object { $_.Name -in @('sandbox', 'secrets') }))
    if ($WithFrontend) { Check 'IIS web.config (SPA fallback + API proxy) shipped inside web/' (Test-Path (Join-Path $pkg 'web/web.config')) }
    Check 'package refuses to overwrite itself' (Throws { & (Join-Path $PSScriptRoot 'New-ReleasePackage.ps1') @args1 })
    Check 'ConfigSync.ps1 shipped' (Test-Path (Join-Path $pkg 'scripts/release/ConfigSync.ps1'))

    Write-Host '== first install (operator copies folders; ConfigSync only)'
    $install = Join-Path $work 'install'
    New-Item -ItemType Directory -Path $install | Out-Null
    foreach ($d in @('api', 'workers', 'migrator', 'web', 'collaboration', 'schema', 'sandbox-defaults', 'docs', 'scripts')) {
        $src = Join-Path $pkg $d
        if (Test-Path -LiteralPath $src) { Copy-Item -Recurse -LiteralPath $src -Destination (Join-Path $install $d) }
    }
    Copy-Item -Force (Join-Path $pkg 'release.json') (Join-Path $install 'release.json')
    & (Join-Path $PSScriptRoot 'ConfigSync.ps1') -InstallDirectory $install -SkipMigrator | Out-Null
    $cfg = Join-Path $install 'sandbox/config'
    Check 'sandbox/config initialised from defaults' ((Test-Path (Join-Path $cfg 'appsettings.json')) -and (Test-Path (Join-Path $cfg 'security.json')))
    Check 'sandbox/data and sandbox/secrets created' ((Test-Path (Join-Path $install 'sandbox/data')) -and (Test-Path (Join-Path $install 'sandbox/secrets')))

    Write-Host '== customise config then sync again'
    $app = Join-Path $cfg 'appsettings.json'
    $json = Get-Content $app -Raw | ConvertFrom-Json
    $json.Raavi.Database.CompatibilityLevel = 130
    $json.Raavi.Database.PSObject.Properties.Remove('CommandTimeoutSeconds')
    $json | ConvertTo-Json -Depth 10 | Set-Content $app -Encoding UTF8
    Remove-Item (Join-Path $cfg 'notifications.json')
    $keys = Join-Path $install 'sandbox/data/keys'
    New-Item -ItemType Directory -Path $keys | Out-Null
    Set-Content (Join-Path $keys 'key-1.xml') '<key/>'
    Set-Content (Join-Path $install 'sandbox/secrets/collaboration.env') 'RAAVI_COLLAB_SERVICE_KEY=secret'
    $keyHash = (Get-FileHash (Join-Path $keys 'key-1.xml')).Hash
    $envHash = (Get-FileHash (Join-Path $install 'sandbox/secrets/collaboration.env')).Hash

    & (Join-Path $PSScriptRoot 'ConfigSync.ps1') -InstallDirectory $install -SkipMigrator | Out-Null
    $after = Get-Content $app -Raw | ConvertFrom-Json
    Check 'administrator value kept' ($after.Raavi.Database.CompatibilityLevel -eq 130)
    Check 'missing setting added by sync' ($after.Raavi.Database.CommandTimeoutSeconds -eq 30)
    Check 'missing file restored by sync' (Test-Path (Join-Path $cfg 'notifications.json'))
    Check 'originals saved in sandbox/backups' (@(Get-ChildItem (Join-Path $install 'sandbox/backups') -Recurse -Filter 'appsettings.json').Count -ge 1)
    Check 'key ring untouched' ((Get-FileHash (Join-Path $keys 'key-1.xml')).Hash -eq $keyHash)
    Check 'secrets untouched' ((Get-FileHash (Join-Path $install 'sandbox/secrets/collaboration.env')).Hash -eq $envHash)

    Write-Host '== refusals'
    Check 'missing migrator rejected' (Throws { & (Join-Path $PSScriptRoot 'ConfigSync.ps1') -InstallDirectory $work -SkipMigrator })
    Check 'sandbox still intact after refusals' ((Get-FileHash (Join-Path $keys 'key-1.xml')).Hash -eq $keyHash)
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue
}

Write-Host "$script:passed checks passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 }
