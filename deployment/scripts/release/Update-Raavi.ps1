<#
.SYNOPSIS
  Installs or upgrades Raavi from a release package while keeping the installation's sandbox/. (Phase 12d, ADR-041)
.DESCRIPTION
  1. verifies release.json and every file hash of the package;
  2. refuses to run while a service still runs from the installation (a locked file in api/, workers/ or migrator/): stop the API pool, workers and collaboration server first, or let the script do it with -AppPool RaaviApi -Service RaaviWorkers (they are started again after a successful upgrade);
  3. copies the current application folders to <Install>/previous/<timestamp>/ (rollback copy; sandbox/ is never copied or changed);
  4. replaces api, workers, migrator, web, collaboration, schema, sandbox-defaults, docs, scripts;
  5. creates sandbox/{config,data,secrets} when missing (first install: config is copied from sandbox-defaults) and runs
     `Raavi.Migrator sandbox sync` (adds settings new in this version, never changes existing values; originals go to sandbox/backups);
  6. installs collaboration dependencies (npm ci --omit=dev) when npm is available;
  7. with -RunMigrator also applies database migrations (needs RAAVI_ConnectionStrings__Migrator or __System in the environment).
  It never starts services, never deletes sandbox/, and never overwrites an existing previous/<timestamp>.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$InstallDirectory,
    [switch]$RunMigrator,
    [switch]$SkipNpm,
    # IIS application pools (e.g. RaaviApi) and Windows services (e.g. RaaviWorkers) to stop before the upgrade and start again after a SUCCESSFUL one.
    # After a failed upgrade they stay stopped on purpose: do not start a half-upgraded installation.
    [string[]]$AppPool = @(),
    [string[]]$Service = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pkg = (Resolve-Path -LiteralPath $PackageDirectory).Path
$manifestPath = Join-Path $pkg 'release.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Not a Raavi release package (release.json missing): $pkg" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.product -ne 'raavi') { throw 'release.json is not a Raavi manifest.' }

Write-Host "== verify package $($manifest.version) ($($manifest.commit))"
foreach ($f in $manifest.files) {
    $path = Join-Path $pkg $f.path
    if (-not (Test-Path -LiteralPath $path)) { throw "Package file missing: $($f.path)" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $f.sha256) { throw "Package file damaged (hash mismatch): $($f.path)" }
}
if (Test-Path -LiteralPath (Join-Path $pkg 'sandbox')) { throw 'The package contains a sandbox folder; refusing to install it.' }

if (-not (Test-Path -LiteralPath $InstallDirectory)) { New-Item -ItemType Directory -Path $InstallDirectory | Out-Null }
$install = (Resolve-Path -LiteralPath $InstallDirectory).Path
if ($install.TrimEnd('\') -eq $pkg.TrimEnd('\')) { throw 'Install directory and package directory are the same.' }
$folders = @($manifest.appFolders)

# Stop what the caller named (IIS pools, Windows services) and remember what was running, to start exactly those afterwards.
$restartPools = @()
$restartServices = @()
if ($AppPool.Count -gt 0) {
    Import-Module WebAdministration -ErrorAction Stop
    foreach ($pool in $AppPool) {
        if (-not (Test-Path "IIS:\AppPools\$pool")) { throw "IIS application pool '$pool' does not exist." }
        if ((Get-WebAppPoolState -Name $pool).Value -ne 'Stopped') {
            if ($PSCmdlet.ShouldProcess($pool, 'stop application pool')) {
                Stop-WebAppPool -Name $pool
                for ($i = 0; $i -lt 60 -and (Get-WebAppPoolState -Name $pool).Value -ne 'Stopped'; $i++) { Start-Sleep -Milliseconds 500 }
                if ((Get-WebAppPoolState -Name $pool).Value -ne 'Stopped') { throw "Application pool '$pool' did not stop in 30 s." }
                $restartPools += $pool
            }
        }
    }
}
foreach ($svc in $Service) {
    $s = Get-Service -Name $svc -ErrorAction Stop
    if ($s.Status -ne 'Stopped') {
        if ($PSCmdlet.ShouldProcess($svc, 'stop service')) {
            Stop-Service -Name $svc -ErrorAction Stop
            $s.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
            $restartServices += $svc
        }
    }
}

# Services must be stopped: a locked file in the current api/ folder means something still runs from it.
foreach ($dll in @('api', 'workers', 'migrator') | ForEach-Object { Join-Path $install "$_" } | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter '*.dll' -File }) {
    try { $s = [System.IO.File]::Open($dll.FullName, 'Open', 'ReadWrite', 'None'); $s.Dispose() }
    catch { throw "A service seems to be running from $($dll.DirectoryName) (file locked: $($dll.Name)). Stop API, workers and collaboration server first." }
}

$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$previous = Join-Path $install "previous/$stamp"
if (Test-Path -LiteralPath $previous) { throw "Rollback folder exists: $previous" }
$firstInstall = $true
foreach ($d in $folders) {
    $current = Join-Path $install $d
    if (Test-Path -LiteralPath $current) {
        $firstInstall = $false
        if ($PSCmdlet.ShouldProcess($current, 'keep a rollback copy')) {
            New-Item -ItemType Directory -Path $previous -Force | Out-Null
            Copy-Item -Recurse -LiteralPath $current -Destination (Join-Path $previous $d)
        }
    }
}
if (-not $firstInstall) { Write-Host "== rollback copy of the previous version: $previous" }

foreach ($d in $folders) {
    $current = Join-Path $install $d
    if ($PSCmdlet.ShouldProcess($current, 'replace from package')) {
        if ($d -eq 'collaboration' -and (Test-Path -LiteralPath (Join-Path $current 'node_modules'))) {
            # keep installed dependencies only when the lock file is unchanged; otherwise npm ci below rebuilds them
            $oldLock = Join-Path $current 'package-lock.json'
            $same = (Test-Path -LiteralPath $oldLock) -and ((Get-FileHash $oldLock).Hash -eq (Get-FileHash (Join-Path $pkg 'collaboration/package-lock.json')).Hash)
            if ($same) { Move-Item -LiteralPath (Join-Path $current 'node_modules') -Destination (Join-Path $install 'node_modules.keep') }
        }
        if (Test-Path -LiteralPath $current) { Remove-Item -Recurse -Force -LiteralPath $current }
        Copy-Item -Recurse -LiteralPath (Join-Path $pkg $d) -Destination $current
        if ($d -eq 'collaboration' -and (Test-Path -LiteralPath (Join-Path $install 'node_modules.keep'))) {
            Move-Item -LiteralPath (Join-Path $install 'node_modules.keep') -Destination (Join-Path $current 'node_modules')
        }
    }
}

# Sandbox: created when missing, never replaced.
$sandbox = Join-Path $install 'sandbox'
foreach ($sub in @('config', 'data', 'secrets')) {
    $dir = Join-Path $sandbox $sub
    if (-not (Test-Path -LiteralPath $dir)) { if ($PSCmdlet.ShouldProcess($dir, 'create')) { New-Item -ItemType Directory -Path $dir -Force | Out-Null } }
}
$sandboxConfig = Join-Path $sandbox 'config'
if (-not (Test-Path -LiteralPath (Join-Path $sandboxConfig 'appsettings.json')) -and $PSCmdlet.ShouldProcess($sandboxConfig, 'initialise from sandbox-defaults')) {
    Write-Host '== first install: sandbox/config initialised from sandbox-defaults (edit it: connection strings, hosts, secrets belong in sandbox/secrets or RAAVI_ variables)'
    Copy-Item -Recurse -Force -Path (Join-Path $install 'sandbox-defaults/config/*') -Destination $sandboxConfig
}

if ($PSCmdlet.ShouldProcess($sandboxConfig, 'sandbox sync')) {
    Write-Host '== sandbox sync'
    $env:RAAVI_SANDBOX_PATH = $sandboxConfig
    $env:RAAVI_SANDBOX_DEFAULTS_PATH = Join-Path $install 'sandbox-defaults/config'
    & dotnet (Join-Path $install 'migrator/Raavi.Migrator.dll') sandbox sync
    if ($LASTEXITCODE -ne 0) { throw "sandbox sync failed (exit $LASTEXITCODE). The previous version is in $previous" }
}

$collab = Join-Path $install 'collaboration'
if (-not $SkipNpm -and -not (Test-Path -LiteralPath (Join-Path $collab 'node_modules'))) {
    if (Get-Command npm -ErrorAction SilentlyContinue) {
        if ($PSCmdlet.ShouldProcess($collab, 'npm ci --omit=dev')) {
            Write-Host '== collaboration dependencies'
            Push-Location $collab
            try { npm ci --omit=dev --silent; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' } } finally { Pop-Location }
        }
    }
    else { Write-Warning 'npm not found: install collaboration dependencies yourself (npm ci --omit=dev in the collaboration folder).' }
}

if ($RunMigrator -and $PSCmdlet.ShouldProcess('database', 'apply migrations')) {
    Write-Host '== database migrations'
    & dotnet (Join-Path $install 'migrator/Raavi.Migrator.dll')
    if ($LASTEXITCODE -ne 0) { throw "Migrator failed (exit $LASTEXITCODE). Roll back application files from $previous; restore the database from backup if migrations partially ran." }
}

foreach ($pool in $restartPools) { Start-WebAppPool -Name $pool; Write-Host "started application pool $pool" }
foreach ($svc in $restartServices) { Start-Service -Name $svc; Write-Host "started service $svc" }
$stillStopped = ($AppPool + $Service).Count -eq 0
Write-Host "Done: Raavi $($manifest.version) is in $install.$(if ($stillStopped) { ' Start the services.' })$(if (Test-Path -LiteralPath $previous) { " Rollback copy: $previous" })"
