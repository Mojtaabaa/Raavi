<#
.SYNOPSIS
  Builds a Raavi release package (a folder and a zip) that can be installed or upgraded with Update-Raavi.ps1. (Phase 12d, ADR-041)
.DESCRIPTION
  Layout of the package (it mirrors the installation layout, so the application finds sandbox/ and sandbox-defaults/ by walking up):
    api/ workers/ migrator/   dotnet publish output (Release)
    web/                      built SPA (frontend/dist)
    collaboration/            src, package.json, package-lock.json (node_modules are installed on the target: npm ci --omit=dev)
    schema/                   editor manifests the collaboration server reads
    sandbox-defaults/         the settings this version ships  (REPLACED on upgrade)
    docs/                     deployment documents
    release.json              version, git commit, build time, file list with SHA-256
  The package NEVER contains sandbox/ (settings, key ring, secrets) - that belongs to the installation. The script fails if one slipped in.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) 'artifacts'),
    [switch]$SkipFrontend,
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._+-]{0,63}$') { throw "Invalid version '$Version'." }
$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$name = "raavi-$Version"
$target = Join-Path $OutputDirectory $name
if (Test-Path -LiteralPath $target) { throw "Package folder already exists (refusing to overwrite): $target" }
New-Item -ItemType Directory -Path $target | Out-Null

function Invoke-Step([string]$Title, [scriptblock]$Action) {
    Write-Host "== $Title"
    & $Action
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "$Title failed (exit $LASTEXITCODE)." }
}

foreach ($project in @('Raavi.Api:api', 'Raavi.Workers:workers', 'Raavi.Migrator:migrator')) {
    $p, $dir = $project.Split(':')
    Invoke-Step "publish $p" { dotnet publish (Join-Path $repo "backend/$p/$p.csproj") -c Release -o (Join-Path $target $dir) --nologo -v quiet -p:UseAppHost=false }
}

if (-not $SkipFrontend) {
    Invoke-Step 'build frontend' {
        Push-Location (Join-Path $repo 'frontend')
        try { npm ci --silent; if ($LASTEXITCODE -eq 0) { npm run build --silent } } finally { Pop-Location }
    }
    Copy-Item -Recurse -LiteralPath (Join-Path $repo 'frontend/dist') -Destination (Join-Path $target 'web')
}

Write-Host '== copy collaboration, schema, defaults, docs'
$collab = Join-Path $target 'collaboration'
New-Item -ItemType Directory -Path $collab | Out-Null
Copy-Item -Recurse -LiteralPath (Join-Path $repo 'collaboration/src') -Destination (Join-Path $collab 'src')
foreach ($f in @('package.json', 'package-lock.json', 'tsconfig.json')) { Copy-Item -LiteralPath (Join-Path $repo "collaboration/$f") -Destination $collab }
Copy-Item -Recurse -LiteralPath (Join-Path $repo 'schema') -Destination (Join-Path $target 'schema')
Copy-Item -Recurse -LiteralPath (Join-Path $repo 'sandbox-defaults') -Destination (Join-Path $target 'sandbox-defaults')
New-Item -ItemType Directory -Path (Join-Path $target 'docs') | Out-Null
Copy-Item -Recurse -LiteralPath (Join-Path $repo 'docs/deployment') -Destination (Join-Path $target 'docs/deployment')
Copy-Item -LiteralPath (Join-Path $repo 'sandbox/README.md') -Destination (Join-Path $target 'docs/SANDBOX.md')
New-Item -ItemType Directory -Path (Join-Path $target 'scripts') | Out-Null
Copy-Item -Recurse -LiteralPath (Join-Path $repo 'deployment/scripts/release') -Destination (Join-Path $target 'scripts/release')
Copy-Item -Recurse -LiteralPath (Join-Path $repo 'deployment/scripts/backup') -Destination (Join-Path $target 'scripts/backup')

# Safety: never ship an installation's own files.
$bad = Get-ChildItem -LiteralPath $target -Recurse -Force -Directory | Where-Object { $_.Name -in @('sandbox', 'secrets', 'DataProtection-Keys') -or ($_.FullName -like '*\data\keys') }
if ($bad) { throw "The package must not contain instance folders: $($bad.FullName -join ', ')" }
$localFiles = Get-ChildItem -LiteralPath $target -Recurse -Force -File | Where-Object { $_.Name -like '*.Local.json' -or $_.Name -like '*.local' -or $_.Name -eq 'collaboration.env' }
if ($localFiles) { throw "The package must not contain local/secret files: $($localFiles.FullName -join ', ')" }

$commit = ''
try { $commit = (git -C $repo rev-parse --short HEAD 2>$null) } catch { $commit = '' }
$files = Get-ChildItem -LiteralPath $target -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ path = $_.FullName.Substring($target.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$manifest = [ordered]@{
    product = 'raavi'; version = $Version; commit = "$commit"; builtUtc = [DateTime]::UtcNow.ToString('o')
    frontendIncluded = (-not $SkipFrontend.IsPresent)
    appFolders = @(@('api', 'workers', 'migrator', 'web', 'collaboration', 'schema', 'sandbox-defaults', 'docs', 'scripts') | Where-Object { Test-Path -LiteralPath (Join-Path $target $_) })
    files = @($files)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $target 'release.json') -Encoding UTF8

$zip = "$target.zip"
if (Test-Path -LiteralPath $zip) { throw "Zip already exists (refusing to overwrite): $zip" }
Compress-Archive -Path (Join-Path $target '*') -DestinationPath $zip
Write-Host "Package: $target"
Write-Host "Zip:     $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB, $(@($files).Count) files)"
