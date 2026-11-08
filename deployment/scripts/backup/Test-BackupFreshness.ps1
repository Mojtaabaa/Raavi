<#
.SYNOPSIS
  Monitoring check: are the scheduled backups recent and the last runs successful? (Phase 12f, ADR-042)
.DESCRIPTION
  Reads <BackupDirectory>/status/<db>-<mode>.json written by Invoke-ScheduledBackup.ps1 and also checks that the last successful backup file still exists
  with the size its manifest recorded. Exit code 0 = fine, 1 = a check failed (stale, last run failed, file missing), 2 = could not check.
  A mode is only checked when you give its maximum age:
    -MaxFullAgeHours 30  -MaxDifferentialAgeHours 8  -MaxLogAgeMinutes 45
  Wire the exit code into your monitoring (Nagios/Zabbix/Task Scheduler "run if failed" ...). A status file that does not exist counts as a failure:
  a backup that never ran is the most common way to find out too late.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$BackupDirectory,
    [ValidateRange(1, 8760)][int]$MaxFullAgeHours,
    [ValidateRange(1, 8760)][int]$MaxDifferentialAgeHours,
    [ValidateRange(1, 525600)][int]$MaxLogAgeMinutes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not ($MaxFullAgeHours -or $MaxDifferentialAgeHours -or $MaxLogAgeMinutes)) { Write-Host 'Give at least one of -MaxFullAgeHours, -MaxDifferentialAgeHours, -MaxLogAgeMinutes.'; exit 2 }

$bad = 0
function Check-Mode([string]$Mode, [TimeSpan]$MaxAge) {
    $statusFile = Join-Path $BackupDirectory "status/$Database-$Mode.json"
    if (-not (Test-Path -LiteralPath $statusFile)) { Write-Host "FAIL $Mode : no status file (the backup has never run?)"; return 1 }
    $s = Get-Content -LiteralPath $statusFile -Raw | ConvertFrom-Json
    $problems = @()
    if ([int]$s.consecutiveFailures -gt 0) { $problems += "last $($s.consecutiveFailures) run(s) failed: $($s.lastError)" }
    if (-not $s.lastSuccessUtc) { $problems += 'never succeeded' }
    else {
        $age = [DateTime]::UtcNow - ([DateTime]::Parse($s.lastSuccessUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)).ToUniversalTime()
        if ($age -gt $MaxAge) { $problems += ('last success {0:N1} h ago (limit {1:N1} h)' -f $age.TotalHours, $MaxAge.TotalHours) }
        $file = Join-Path $BackupDirectory $s.lastBackupFile
        if (-not (Test-Path -LiteralPath $file)) { $problems += "backup file missing: $($s.lastBackupFile)" }
        elseif (Test-Path -LiteralPath "$file.json") {
            $m = Get-Content -LiteralPath "$file.json" -Raw | ConvertFrom-Json
            if ((Get-Item -LiteralPath $file).Length -ne [long]$m.sizeBytes) { $problems += "backup file size differs from its manifest: $($s.lastBackupFile)" }
        }
        else { $problems += "manifest missing for $($s.lastBackupFile)" }
    }
    if ($problems.Count -gt 0) { Write-Host "FAIL $Mode : $($problems -join '; ')"; return 1 }
    Write-Host "ok   $Mode : $($s.lastBackupFile)"
    return 0
}

try {
    if ($MaxFullAgeHours) { $bad += Check-Mode 'Full' ([TimeSpan]::FromHours($MaxFullAgeHours)) }
    if ($MaxDifferentialAgeHours) { $bad += Check-Mode 'Differential' ([TimeSpan]::FromHours($MaxDifferentialAgeHours)) }
    if ($MaxLogAgeMinutes) { $bad += Check-Mode 'Log' ([TimeSpan]::FromMinutes($MaxLogAgeMinutes)) }
}
catch { Write-Host "Could not check: $($_.Exception.Message)"; exit 2 }
if ($bad -gt 0) { exit 1 }
exit 0
